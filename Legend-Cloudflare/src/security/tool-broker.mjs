import { canonicalJson, hmacSign, parseJsonBytes, readBoundedBody, sha256 } from './crypto.mjs';
import { isIdentifier, isNonnegativeInteger, signingMessage } from './authenticate.mjs';
import { requireSecurity, SecurityError } from './errors.mjs';

const encoder = new TextEncoder();

function scopeFrom(context) {
  return Object.fromEntries(['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId', 'roles',
    'authorizationVersion'].map(key => [key, context[key]]));
}

/** Azure binds/consumes approval for this digest; Cloudflare never grants it. */
export async function toolActionDigest(context, call, environment) {
  return sha256(canonicalJson({ version: 'legend-tool-action.v1', scope: scopeFrom(context),
    requestId: context.requestId, environment, name: call.name, arguments: call.arguments }));
}

function callbackConfiguration(env) {
  requireSecurity(env.LEGEND_TOOL_CALLBACK_ENABLED === 'true', 'tools_unavailable', 503);
  let url;
  try { url = new URL(env.LEGEND_AZURE_TOOL_CALLBACK_URL); }
  catch { throw new SecurityError('tool_callback_configuration_invalid', 503); }
  requireSecurity(url.protocol === 'https:' && !url.username && !url.password && !url.search && !url.hash &&
    !url.hostname.includes(':') && !/^(localhost|.*\.localhost|.*\.local|[0-9.]+)$/i.test(url.hostname) &&
    url.port === '', 'tool_callback_configuration_invalid', 503);
  requireSecurity(isIdentifier(env.LEGEND_TOOL_CALLBACK_KEY_ID) && isIdentifier(env.LEGEND_DEPLOYMENT_ENVIRONMENT),
    'tool_callback_configuration_invalid', 503);
  const cost = Number(env.LEGEND_TOOL_MAX_COST_MICROUSD);
  requireSecurity(isNonnegativeInteger(cost) && cost > 0 && cost <= 1_000_000_000,
    'tool_cost_ceiling_missing', 503);
  return { url, cost, environment: env.LEGEND_DEPLOYMENT_ENVIRONMENT };
}

async function abortable(operation, signal) {
  requireSecurity(!signal.aborted, 'tool_cancelled', 499);
  let listener;
  try {
    return await Promise.race([operation(), new Promise((_, reject) => {
      listener = () => reject(new SecurityError('tool_cancelled', 499));
      signal.addEventListener('abort', listener, { once: true });
      if (signal.aborted) listener();
    })]);
  } finally { signal.removeEventListener('abort', listener); }
}

/** Every call returns to the existing Azure authority for current object ACLs. */
export function createToolBroker({ env, context: trustedContext, budget, fetcher = fetch, now = Date.now }) {
  return Object.freeze({
    async execute({ context, call, idempotencyKey, signal: callerSignal }) {
      requireSecurity(context === trustedContext, 'context_substitution');
      requireSecurity(now() < context.expiresAt && now() < context.deadlineUnixMs, 'tool_context_expired', 401);
      requireSecurity(!callerSignal?.aborted, 'tool_cancelled', 499);
      requireSecurity(call && isIdentifier(call.id) && isIdentifier(call.name) &&
        context.allowedTools.includes(call.name), 'tool_not_authorized');
      requireSecurity(call.arguments && typeof call.arguments === 'object' && !Array.isArray(call.arguments),
        'tool_arguments_invalid', 400);
      requireSecurity(encoder.encode(canonicalJson(call.arguments)).length <= 32768, 'tool_arguments_too_large', 413);
      // Keep the approved digest, transmitted arguments and receipt comparison
      // identical even if the caller still holds mutable provider objects.
      call = JSON.parse(canonicalJson({ id: call.id, name: call.name, arguments: call.arguments }));
      requireSecurity(idempotencyKey === await sha256(`${context.requestId}:tool:${call.id}`), 'tool_idempotency_invalid', 400);
      const config = callbackConfiguration(env);
      const actionDigest = await toolActionDigest(context, call, config.environment);
      const timestamp = now();
      const expiresAt = Math.min(context.expiresAt, context.deadlineUnixMs, timestamp + 30000);
      const nonce = crypto.randomUUID().replaceAll('-', '');
      const body = JSON.stringify({ version: 'legend-tool-callback.v1', requestId: context.requestId,
        scope: scopeFrom(context), contextDigest: context.contextDigest, call, actionDigest,
        environment: config.environment, idempotencyKey, issuedAt: timestamp, expiresAt,
        maxCostMicrousd: config.cost });
      const signature = await hmacSign(env.LEGEND_TOOL_CALLBACK_SECRET, signingMessage({ method: 'POST',
        path: config.url.pathname, keyId: env.LEGEND_TOOL_CALLBACK_KEY_ID, timestamp, nonce, bodyDigest: await sha256(body) }));
      const reservationId = await sha256(`tool-reservation:${idempotencyKey}`);
      // Reserve is deliberately not raced with cancellation: no dispatch without
      // its durable receipt, and a canceled pre-dispatch reservation is refunded.
      await budget.reserve(context, { requestId: context.requestId, reservationId,
        maxCostMicrousd: config.cost, deadlineUnixMs: context.deadlineUnixMs });
      const cancellation = new AbortController();
      const signal = callerSignal ? AbortSignal.any([callerSignal, cancellation.signal]) : cancellation.signal;
      const timer = setTimeout(() => cancellation.abort(), Math.max(0, expiresAt - now()));
      let dispatched = false;
      let receipt;
      let error;
      try {
        receipt = await abortable(async () => {
          dispatched = true;
          const response = await fetcher(config.url.href, {
            method: 'POST', body, signal, redirect: 'error', cache: 'no-store', headers: {
              'Content-Type': 'application/json', 'Cache-Control': 'no-store',
              'X-Legend-Key-Id': env.LEGEND_TOOL_CALLBACK_KEY_ID, 'X-Legend-Timestamp': String(timestamp),
              'X-Legend-Nonce': nonce, 'X-Legend-Signature': signature,
            },
          });
          requireSecurity(response.ok && response.status === 200, 'tool_authorization_or_execution_failed', 502);
          const value = parseJsonBytes(await readBoundedBody(response.body, 32768));
          requireSecurity(value.version === 'legend-tool-receipt.v1' && value.reauthorized === true &&
            value.requestId === context.requestId && value.contextDigest === context.contextDigest &&
            value.toolCallId === call.id && value.actionDigest === actionDigest && value.idempotencyKey === idempotencyKey &&
            value.authorizationVersion === context.authorizationVersion && Object.hasOwn(value, 'output'),
          'tool_receipt_invalid', 502);
          requireSecurity(value.usage && (
            (value.usage.known === true && isNonnegativeInteger(value.usage.costMicrousd) &&
              (value.usage.costEvidence === undefined || value.usage.costEvidence === 'provider_usage')) ||
            (value.usage.known === false && value.usage.costEvidence === 'reserved_upper_bound' &&
              value.usage.costMicrousd === config.cost)), 'tool_usage_invalid', 502);
          canonicalJson(value.output);
          return value;
        }, signal);
      } catch (caught) {
        error = caught instanceof SecurityError ? caught : new SecurityError('tool_callback_failed', 502);
      } finally { clearTimeout(timer); }
      const usageKnown = !dispatched || Boolean(receipt?.usage.known);
      // Only an authenticated, reauthorized and action-bound successful receipt
      // may establish completion separately from billing certainty. The full
      // operator reservation remains charged; no provider usage is invented.
      const executionCompleted = receipt?.usage.known === false &&
        receipt.usage.costEvidence === 'reserved_upper_bound' && receipt.usage.costMicrousd === config.cost;
      let settled;
      try {
        settled = await budget.settle(context, { reservationId, usageKnown,
          actualCostMicrousd: receipt?.usage.costMicrousd ?? 0,
          ...(executionCompleted ? { executionCompleted: true } : {}) });
      } catch (settlementError) {
        // The durable full debit already exists even if a settlement receipt is
        // lost. Preserve its conservative evidence in the failed response.
        const failure = settlementError instanceof SecurityError ? settlementError : new SecurityError('governance_unavailable', 503);
        failure.usage ??= { costMicrousd: Math.max(config.cost, receipt?.usage.costMicrousd ?? 0),
          costEvidence: 'reserved_upper_bound' };
        throw failure;
      }
      const usage = { costMicrousd: settled.chargedMicrousd,
        costEvidence: usageKnown ? 'provider_usage' : 'reserved_upper_bound' };
      if (!error && !usageKnown && !executionCompleted) error = new SecurityError('tool_usage_unavailable', 502);
      if (error) { error.usage = usage; throw error; }
      return { output: receipt.output, usage };
    },
  });
}
