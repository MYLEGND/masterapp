import { canonicalJson, deepFreeze, hmacVerify, parseJsonBytes, readBoundedBody, sha256 } from './crypto.mjs';
import { requireSecurity } from './errors.mjs';

export const SIGNATURE_VERSION = 'legend-service.v1';
export const RESPOND_PATH = '/v1/legend/respond';
const TOKEN = /^[A-Za-z0-9_.:@-]{1,128}$/;
const NONCE = /^[A-Za-z0-9_-]{22,128}$/;
const MAXIMUM_BODY_BYTES = 1_048_576;
const CLOCK_SKEW_MS = 5_000;
const CONTEXT_LIFETIME_MS = 120_000;

export function signingMessage({ method, path, keyId, timestamp, nonce, bodyDigest }) {
  return [SIGNATURE_VERSION, method, path, keyId, String(timestamp), nonce, bodyDigest].join('\n');
}

export function isIdentifier(value) { return typeof value === 'string' && TOKEN.test(value); }
export function isNonnegativeInteger(value) { return Number.isSafeInteger(value) && value >= 0; }

export function validateScope(scope, accountId) {
  requireSecurity(scope && typeof scope === 'object' && !Array.isArray(scope), 'scope_required');
  for (const key of ['accountId', 'tenantId', 'userId', 'sessionId', 'conversationId', 'authorizationVersion']) {
    requireSecurity(isIdentifier(scope[key]), 'scope_invalid');
  }
  requireSecurity(scope.accountId === accountId, 'account_scope_mismatch');
  requireSecurity(Array.isArray(scope.roles) && scope.roles.length > 0 && scope.roles.length <= 32 &&
    scope.roles.every(isIdentifier) && new Set(scope.roles).size === scope.roles.length, 'scope_roles_invalid');
}

function validateEnvelope(envelope, env, now, timestamp) {
  requireSecurity(envelope && envelope.version === 'legend-cloudflare.v1', 'protocol_version_invalid', 400);
  requireSecurity(isIdentifier(envelope.requestId), 'request_id_invalid', 400);
  validateScope(envelope.scope, env.LEGEND_ACCOUNT_ID);
  requireSecurity(Number.isSafeInteger(envelope.issuedAt) && Number.isSafeInteger(envelope.expiresAt) &&
    envelope.issuedAt === timestamp && envelope.expiresAt > envelope.issuedAt &&
    envelope.expiresAt - envelope.issuedAt <= CONTEXT_LIFETIME_MS &&
    envelope.issuedAt <= now + CLOCK_SKEW_MS && envelope.expiresAt > now,
  'context_expired_or_invalid', 401);
  const limits = envelope.limits;
  requireSecurity(limits && Number.isSafeInteger(limits.deadlineUnixMs) && limits.deadlineUnixMs > now &&
    limits.deadlineUnixMs <= envelope.expiresAt, 'deadline_invalid', 400);
  for (const field of ['maxOutputTokens', 'maxIterations', 'maxModelCalls', 'maxToolCalls', 'maxCostMicrousd']) {
    requireSecurity(isNonnegativeInteger(limits[field]), 'limits_invalid', 400);
  }
  requireSecurity(limits.maxOutputTokens > 0 && limits.maxOutputTokens <= 65536 &&
    limits.maxIterations > 0 && limits.maxIterations <= 32 &&
    limits.maxModelCalls > 0 && limits.maxModelCalls <= 64 && limits.maxToolCalls <= 64,
  'limits_invalid', 400);
  requireSecurity(typeof envelope.stream === 'boolean', 'stream_invalid', 400);
  requireSecurity(envelope.task && typeof envelope.task === 'object' &&
    Array.isArray(envelope.task.messages) && envelope.task.messages.length > 0 &&
    envelope.task.messages.length <= 256 && Array.isArray(envelope.task.tools) &&
    envelope.task.tools.length <= 64, 'task_invalid', 400);
  // Walk once before freezing to reject pathological nesting, even on a service-signed body.
  canonicalJson(envelope);
}

/** Authenticates only Azure-signed bytes. A browser/model cannot supply context. */
export async function authenticateRequest(request, env, { now = Date.now } = {}) {
  const url = new URL(request.url);
  requireSecurity(request.method === 'POST' && url.pathname === RESPOND_PATH && !url.search,
    'request_target_invalid', 404);
  requireSecurity(isIdentifier(env.LEGEND_ACCOUNT_ID), 'account_configuration_missing', 503);
  requireSecurity((request.headers.get('content-type') ?? '').split(';', 1)[0].trim().toLowerCase() === 'application/json' &&
    !request.headers.has('content-encoding'), 'content_type_invalid', 415);
  const keyId = request.headers.get('X-Legend-Key-Id');
  const timestampText = request.headers.get('X-Legend-Timestamp');
  const nonce = request.headers.get('X-Legend-Nonce');
  requireSecurity(isIdentifier(keyId) && typeof nonce === 'string' && NONCE.test(nonce) &&
    typeof timestampText === 'string' && /^[1-9][0-9]{12}$/.test(timestampText), 'signature_headers_invalid', 401);
  const timestamp = Number(timestampText);
  const time = now();
  requireSecurity(timestamp <= time + CLOCK_SKEW_MS && time - timestamp <= CONTEXT_LIFETIME_MS,
    'signature_expired', 401);
  let keys;
  try { keys = JSON.parse(env.LEGEND_SERVICE_KEYS_JSON); }
  catch { requireSecurity(false, 'service_keys_missing', 503); }
  requireSecurity(keys && typeof keys === 'object' && !Array.isArray(keys), 'service_keys_missing', 503);
  requireSecurity(Object.hasOwn(keys, keyId), 'service_key_unknown', 401);
  const bytes = await readBoundedBody(request.body, MAXIMUM_BODY_BYTES);
  const bodyDigest = await sha256(bytes);
  requireSecurity(await hmacVerify(keys[keyId], signingMessage({ method: request.method, path: url.pathname,
    keyId, timestamp, nonce, bodyDigest }), request.headers.get('X-Legend-Signature')), 'signature_invalid', 401);
  const envelope = parseJsonBytes(bytes);
  validateEnvelope(envelope, env, time, timestamp);
  const allowedTools = envelope.task.tools.map(tool => tool?.name ?? tool?.function?.name);
  requireSecurity(allowedTools.every(isIdentifier) && new Set(allowedTools).size === allowedTools.length,
    'tool_catalog_invalid', 400);
  const scope = { ...envelope.scope };
  const context = {
    ...scope, requestId: envelope.requestId, issuedAt: envelope.issuedAt, expiresAt: envelope.expiresAt,
    deadlineUnixMs: envelope.limits.deadlineUnixMs, maxCostMicrousd: envelope.limits.maxCostMicrousd,
    keyId, nonce, bodyDigest, allowedTools,
    contextDigest: await sha256(canonicalJson({ scope, requestId: envelope.requestId, bodyDigest })),
  };
  return { envelope: deepFreeze(envelope), context: deepFreeze(context) };
}
