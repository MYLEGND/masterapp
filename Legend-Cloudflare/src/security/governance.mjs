import { canonicalJson, parseJsonBytes, readBoundedBody, sha256 } from './crypto.mjs';
import { isIdentifier, isNonnegativeInteger, validateScope } from './authenticate.mjs';
import { requireSecurity, SecurityError, securityErrorResponse } from './errors.mjs';
import { MODEL_REGISTRY, RuntimeFailure, estimateCostMicrousd, resolveExecutionPolicy } from '../runtime/registry.mjs';

const MAX_REQUEST_MS = 120_000;
const MAX_RECORDS_PER_REQUEST = 128;
const QUALIFICATION_OUTPUT_RESERVATION_TOKENS = 1024;

async function requestBudgetFor(context, env, policy, now) {
  const ordinary = { ceilingMicrousd: policy.requestMicrousd, qualificationDigest: null };
  // Only the reviewed colocated qualification grants can raise a request's
  // ceiling. Other modes and missing grants retain the existing operator cap.
  if (env.LEGEND_RUNTIME_MODE !== 'founder_baseline' || env.LEGEND_QUALIFICATION_POLICIES_JSON === undefined)
    return ordinary;
  requireSecurity(Array.isArray(context.allowedTools) && context.allowedTools.every(isIdentifier), 'governance_context_invalid');
  // Reuse the runtime's policy authority. Every reconstructed field is already
  // authenticated; no body flag, client model ID or caller-supplied grant enters.
  let execution;
  try {
    execution = resolveExecutionPolicy(env, {
      requestId: context.requestId, scope: context,
      limits: { deadlineUnixMs: context.deadlineUnixMs },
      task: { tools: context.allowedTools.map(name => ({ name })) },
    }, context, now);
  } catch (error) {
    if (error instanceof RuntimeFailure)
      throw new SecurityError(error.code, error.code.includes('configuration') || error.code.endsWith('_required') ? 503 : 403);
    throw error;
  }
  if (execution.mode !== 'qualification') return ordinary;
  const model = MODEL_REGISTRY.find(candidate => candidate.id === execution.modelId);
  requireSecurity(model, 'qualification_budget_model_invalid', 503);
  // Registry prices/context remain the one cost source. Larger output reserves
  // fail the request budget; this does not reclassify a candidate as incapable.
  const ceilingMicrousd = estimateCostMicrousd(model, model.contextTokens, QUALIFICATION_OUTPUT_RESERVATION_TOKENS);
  const grant = JSON.parse(env.LEGEND_QUALIFICATION_POLICIES_JSON).policies.find(candidate => candidate.serviceKeyId === context.keyId);
  requireSecurity(grant && grant.modelId === model.id, 'qualification_budget_policy_changed');
  return { ceilingMicrousd, qualificationDigest: await sha256(canonicalJson({
    version: 'legend-qualification-request-budget.v1', grant, ceilingMicrousd,
  })) };
}

function policyFrom(env) {
  let policy;
  try { policy = JSON.parse(env.LEGEND_BUDGET_POLICY_JSON); }
  catch { throw new SecurityError('budget_configuration_missing', 503); }
  requireSecurity(policy && typeof policy === 'object', 'budget_configuration_invalid', 503);
  for (const kind of ['request', 'user', 'tenant', 'account']) {
    requireSecurity(isNonnegativeInteger(policy[`${kind}Microusd`]) &&
      policy[`${kind}Microusd`] <= 1_000_000_000_000 &&
      isNonnegativeInteger(policy[`${kind}Concurrency`]) && policy[`${kind}Concurrency`] <= 10000,
    'budget_configuration_invalid', 503);
  }
  requireSecurity(['fixed', 'calendar-month', 'lifetime'].includes(policy.period) &&
    (policy.period !== 'fixed' || (Number.isSafeInteger(policy.periodMs) && policy.periodMs >= 60_000 &&
    policy.periodMs <= 31 * 86400_000)) && Number.isSafeInteger(policy.retentionMs) &&
    policy.retentionMs >= MAX_REQUEST_MS && policy.retentionMs <= 31 * 86400_000,
  'budget_configuration_invalid', 503);
  return Object.freeze(policy);
}

function scopeIdentity(context) {
  return [context.accountId, context.tenantId, context.userId, context.sessionId, context.conversationId];
}

function validateContext(context, env) {
  validateScope(context, env.LEGEND_ACCOUNT_ID);
  requireSecurity(isIdentifier(context.requestId) && /^[a-f0-9]{64}$/.test(context.contextDigest ?? '') &&
    /^[a-f0-9]{64}$/.test(context.bodyDigest ?? '') && /^[A-Za-z0-9_-]{22,128}$/.test(context.nonce ?? '') &&
    isIdentifier(context.keyId) && isNonnegativeInteger(context.maxCostMicrousd) &&
    Number.isSafeInteger(context.issuedAt) && Number.isSafeInteger(context.expiresAt) &&
    Number.isSafeInteger(context.deadlineUnixMs) && context.expiresAt > context.issuedAt &&
    context.expiresAt - context.issuedAt <= MAX_REQUEST_MS && context.deadlineUnixMs <= context.expiresAt,
  'governance_context_invalid');
}

async function identities(context) {
  const hash = parts => sha256(canonicalJson(parts));
  return {
    request: await hash(['request', context.accountId, context.requestId]),
    user: await hash(['user', context.accountId, context.tenantId, context.userId]),
    tenant: await hash(['tenant', context.accountId, context.tenantId]),
    account: await hash(['account', context.accountId]),
    nonce: await hash(['nonce', context.accountId, context.keyId, context.nonce]),
    owner: await hash(scopeIdentity(context)),
  };
}

async function retain(tx, key, value, expiresAt) {
  await tx.put(key, { ...value, expiresAt });
  if (expiresAt !== null) await tx.put(`expiry:${String(expiresAt).padStart(16, '0')}:${key}`, { key, expiresAt });
}

function accountingPeriod(policy, now) {
  if (policy.period === 'lifetime') return { id: 'lifetime', expiresAt: null };
  if (policy.period === 'calendar-month') {
    const date = new Date(now);
    return { id: `month:${date.getUTCFullYear()}-${date.getUTCMonth() + 1}`,
      expiresAt: Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 1) + MAX_REQUEST_MS + policy.retentionMs };
  }
  const index = Math.floor(now / policy.periodMs);
  return { id: `fixed:${policy.periodMs}:${index}`,
    expiresAt: (index + 1) * policy.periodMs + MAX_REQUEST_MS + policy.retentionMs };
}

function ownRequest(record, context, ids) {
  requireSecurity(record && record.owner === ids.owner && record.contextDigest === context.contextDigest &&
    record.bodyDigest === context.bodyDigest, 'request_scope_mismatch');
}

/**
 * One object per Cloudflare billing account is deliberate: splitting user/tenant
 * counters across objects cannot atomically enforce the account ceiling.
 * Deploy this class as a SQLite-backed Durable Object. No network work occurs
 * within a storage transaction; no mutable budget state is process-local.
 */
export class LegendGovernance {
  constructor(state, env, { now = Date.now } = {}) {
    this.state = state;
    this.env = env;
    this.now = now;
  }

  async fetch(request) {
    try {
      requireSecurity(request.method === 'POST' && new URL(request.url).pathname === '/govern', 'governance_target_invalid', 404);
      const { op, context, input = {} } = parseJsonBytes(await readBoundedBody(request.body, 65536));
      const policy = policyFrom(this.env);
      validateContext(context, this.env);
      const ids = await identities(context);
      const now = this.now();
      const result = await this.state.storage.transaction(async tx => {
        if (op === 'status') return this.status(tx, context, ids, policy, now);
        if (op === 'control') return this.control(tx, context, ids, policy, now, input);
        if (op === 'claim') return this.claim(tx, context, ids, policy, now);
        const record = await tx.get(`request:${ids.request}`);
        ownRequest(record, context, ids);
        if (op === 'reserve') return this.reserve(tx, context, ids, policy, now, record, input);
        if (op === 'settle') return this.settle(tx, ids, now, record, input);
        if (op === 'close') {
          await tx.put(`request:${ids.request}`, { ...record, closed: true });
          // Unconfirmed executions retain their concurrency leases through deadline.
          return { closed: true };
        }
        throw new SecurityError('governance_operation_invalid', 400);
      });
      await this.scheduleExpiry();
      return Response.json(result, { headers: { 'Cache-Control': 'no-store, private' } });
    } catch (error) { return securityErrorResponse(error); }
  }

  async founderControlState(tx, ids, policy) {
    const stored = await tx.get(`control:${ids.account}`);
    const spendCapMicrousd = Number.isSafeInteger(stored?.spendCapMicrousd)
      ? Math.min(Math.max(stored.spendCapMicrousd, 0), policy.accountMicrousd)
      : policy.accountMicrousd;
    return {
      paused: stored?.paused === true,
      spendCapMicrousd,
      releaseAuthorizedMicrousd: policy.accountMicrousd,
      updatedAt: Number.isSafeInteger(stored?.updatedAt) ? stored.updatedAt : null,
    };
  }

  async founderStatusSnapshot(tx, ids, policy, now) {
    const period = accountingPeriod(policy, now);
    const quota = await tx.get(`quota:${period.id}:${ids.account}`) ?? { chargedMicrousd: 0 };
    const leases = await tx.get(`leases:${ids.account}`);
    const active = (leases?.active ?? []).filter(lease => lease.until > now);
    const control = await this.founderControlState(tx, ids, policy);
    const chargedMicrousd = Number.isSafeInteger(quota.chargedMicrousd) ? quota.chargedMicrousd : 0;
    return {
      period: policy.period,
      releaseAuthorizedMicrousd: control.releaseAuthorizedMicrousd,
      spendCapMicrousd: control.spendCapMicrousd,
      chargedMicrousd,
      remainingMicrousd: Math.max(0, control.spendCapMicrousd - chargedMicrousd),
      paused: control.paused,
      activeConcurrency: active.length,
      concurrencyLimit: policy.accountConcurrency,
      controlUpdatedAt: control.updatedAt,
    };
  }

  async consumeControlNonce(tx, context, ids) {
    requireSecurity(!(await tx.get(`nonce:${ids.nonce}`)), 'request_replayed', 409);
    await retain(tx, `nonce:${ids.nonce}`, {}, context.expiresAt + 5000);
  }

  async status(tx, context, ids, policy, now) {
    requireSecurity(this.env.LEGEND_RUNTIME_MODE === 'founder_baseline' &&
      Array.isArray(context.roles) && context.roles.length === 1 && context.roles[0] === 'Founder',
    'founder_control_scope_denied');
    await this.consumeControlNonce(tx, context, ids);
    return this.founderStatusSnapshot(tx, ids, policy, now);
  }

  async control(tx, context, ids, policy, now, input) {
    requireSecurity(this.env.LEGEND_RUNTIME_MODE === 'founder_baseline' &&
      Array.isArray(context.roles) && context.roles.length === 1 && context.roles[0] === 'Founder',
    'founder_control_scope_denied');
    await this.consumeControlNonce(tx, context, ids);
    const period = accountingPeriod(policy, now);
    const quota = await tx.get(`quota:${period.id}:${ids.account}`) ?? { chargedMicrousd: 0 };
    const chargedMicrousd = Number.isSafeInteger(quota.chargedMicrousd) ? quota.chargedMicrousd : 0;
    const current = await this.founderControlState(tx, ids, policy);
    let next = current;
    if (input?.action === 'set_pause') {
      requireSecurity(typeof input.paused === 'boolean', 'founder_control_invalid', 400);
      next = { ...current, paused: input.paused, updatedAt: now };
    } else if (input?.action === 'set_spend_cap') {
      requireSecurity(Number.isSafeInteger(input.spendCapMicrousd) && input.spendCapMicrousd >= chargedMicrousd &&
        input.spendCapMicrousd <= policy.accountMicrousd, 'founder_spend_cap_invalid', 400);
      next = { ...current, spendCapMicrousd: input.spendCapMicrousd, updatedAt: now };
    } else throw new SecurityError('founder_control_invalid', 400);
    await tx.put(`control:${ids.account}`, {
      paused: next.paused, spendCapMicrousd: next.spendCapMicrousd, updatedAt: next.updatedAt,
    });
    return this.founderStatusSnapshot(tx, ids, policy, now);
  }

  async claim(tx, context, ids, policy, now) {
    requireSecurity(context.issuedAt <= now + 5000 && context.expiresAt > now && context.deadlineUnixMs > now,
      'context_expired_or_invalid', 401);
    requireSecurity(context.maxCostMicrousd > 0 && policy.requestMicrousd > 0 && policy.accountMicrousd > 0,
      'budget_exhausted', 429);
    const requestBudget = await requestBudgetFor(context, this.env, policy, now);
    requireSecurity(!(await tx.get(`request:${ids.request}`)) && !(await tx.get(`nonce:${ids.nonce}`)), 'request_replayed', 409);
    const expiresAt = context.deadlineUnixMs + policy.retentionMs;
    await retain(tx, `request:${ids.request}`, {
      owner: ids.owner, contextDigest: context.contextDigest, bodyDigest: context.bodyDigest,
      deadlineUnixMs: context.deadlineUnixMs, costLimitMicrousd: Math.min(context.maxCostMicrousd, requestBudget.ceilingMicrousd),
      qualificationBudgetDigest: requestBudget.qualificationDigest,
      chargedMicrousd: 0, reservations: 0, closed: false,
    }, expiresAt);
    await retain(tx, `nonce:${ids.nonce}`, {}, context.expiresAt + 5000);
    return { claimed: true };
  }

  async reserve(tx, context, ids, policy, now, record, input) {
    requireSecurity(!record.closed && now < record.deadlineUnixMs, 'request_closed', 409);
    requireSecurity(input.requestId === context.requestId && isIdentifier(input.reservationId) &&
      Number.isSafeInteger(input.deadlineUnixMs) && input.deadlineUnixMs > now &&
      input.deadlineUnixMs <= record.deadlineUnixMs && isNonnegativeInteger(input.maxCostMicrousd) &&
      input.maxCostMicrousd > 0, 'reservation_invalid', 400);
    const reservationKey = `reservation:${ids.request}:${input.reservationId}`;
    requireSecurity(!(await tx.get(reservationKey)), 'reservation_replayed', 409);
    requireSecurity(record.reservations < MAX_RECORDS_PER_REQUEST, 'reservation_limit_exceeded', 429);
    const requestBudget = await requestBudgetFor(context, this.env, policy, now);
    requireSecurity((record.qualificationBudgetDigest ?? null) === requestBudget.qualificationDigest,
      'qualification_budget_policy_changed');
    const amount = input.maxCostMicrousd;
    requireSecurity(amount <= Math.min(record.costLimitMicrousd, requestBudget.ceilingMicrousd) - record.chargedMicrousd,
      'request_budget_exhausted', 429);
    const period = accountingPeriod(policy, now);
    const founderRequest = this.env.LEGEND_RUNTIME_MODE === 'founder_baseline' &&
      Array.isArray(context.roles) && context.roles.length === 1 && context.roles[0] === 'Founder';
    const founderControl = founderRequest ? await this.founderControlState(tx, ids, policy) : null;
    if (founderControl) requireSecurity(!founderControl.paused, 'founder_inference_paused', 429);
    const quotaKeys = [];
    // All four concurrency dimensions and all three periodic spend dimensions
    // participate in the same transaction as the request's total spend.
    for (const kind of ['request', 'user', 'tenant', 'account']) {
      const leaseKey = `leases:${ids[kind]}`;
      const leaseRecord = await tx.get(leaseKey);
      const active = (leaseRecord?.active ?? []).filter(lease => lease.until > now);
      requireSecurity(active.length < policy[`${kind}Concurrency`], `${kind}_concurrency_exhausted`, 429);
      active.push({ id: reservationKey, until: record.deadlineUnixMs });
      await retain(tx, leaseKey, { active }, Math.max(...active.map(lease => lease.until)) + policy.retentionMs);
      if (kind === 'request') continue;
      const quotaKey = `quota:${period.id}:${ids[kind]}`;
      const quota = await tx.get(quotaKey) ?? { chargedMicrousd: 0 };
      const spendLimit = kind === 'account' && founderControl
        ? Math.min(policy.accountMicrousd, founderControl.spendCapMicrousd)
        : policy[`${kind}Microusd`];
      requireSecurity(amount <= spendLimit - quota.chargedMicrousd, `${kind}_budget_exhausted`, 429);
      await retain(tx, quotaKey, { chargedMicrousd: quota.chargedMicrousd + amount }, period.expiresAt);
      quotaKeys.push(quotaKey);
    }
    await tx.put(`request:${ids.request}`, { ...record, chargedMicrousd: record.chargedMicrousd + amount,
      reservations: record.reservations + 1 });
    await retain(tx, reservationKey, {
      reservedMicrousd: amount, quotaKeys, leaseKeys: ['request', 'user', 'tenant', 'account'].map(kind => `leases:${ids[kind]}`),
      settled: false,
    }, record.expiresAt);
    return { reservationId: input.reservationId, reservedMicrousd: amount };
  }

  async settle(tx, ids, now, record, input) {
    requireSecurity(isIdentifier(input.reservationId) && typeof input.usageKnown === 'boolean' &&
      (!input.usageKnown || isNonnegativeInteger(input.actualCostMicrousd)) &&
      (input.executionCompleted === undefined || typeof input.executionCompleted === 'boolean'), 'settlement_invalid', 400);
    // Supplied only by the trusted broker after a verified completed tool
    // receipt. Model usage uncertainty never establishes execution completion.
    const executionCompleted = input.executionCompleted === true;
    const key = `reservation:${ids.request}:${input.reservationId}`;
    const reservation = await tx.get(key);
    requireSecurity(reservation, 'reservation_missing', 409);
    const chargedMicrousd = input.usageKnown ? input.actualCostMicrousd : reservation.reservedMicrousd;
    requireSecurity(chargedMicrousd <= 1_000_000_000_000, 'settlement_invalid', 400);
    if (reservation.settled) {
      requireSecurity(reservation.usageKnown === input.usageKnown && reservation.chargedMicrousd === chargedMicrousd &&
        (reservation.executionCompleted ?? false) === executionCompleted,
        'settlement_conflict', 409);
      return { chargedMicrousd, usageKnown: reservation.usageKnown, overReservation: chargedMicrousd > reservation.reservedMicrousd };
    }
    const difference = chargedMicrousd - reservation.reservedMicrousd;
    for (const quotaKey of reservation.quotaKeys) {
      const quota = await tx.get(quotaKey);
      requireSecurity(quota && Number.isSafeInteger(quota.chargedMicrousd + difference), 'budget_state_missing', 503);
      await tx.put(quotaKey, { ...quota, chargedMicrousd: quota.chargedMicrousd + difference });
    }
    requireSecurity(Number.isSafeInteger(record.chargedMicrousd + difference), 'budget_state_invalid', 503);
    await tx.put(`request:${ids.request}`, { ...record, chargedMicrousd: record.chargedMicrousd + difference });
    if (input.usageKnown || executionCompleted) {
      for (const leaseKey of reservation.leaseKeys) {
        const leases = await tx.get(leaseKey);
        if (leases) await tx.put(leaseKey, { ...leases, active: leases.active.filter(lease => lease.id !== key && lease.until > now) });
      }
    }
    await tx.put(key, { ...reservation, settled: true, chargedMicrousd, usageKnown: input.usageKnown, executionCompleted });
    return { chargedMicrousd, usageKnown: input.usageKnown, overReservation: chargedMicrousd > reservation.reservedMicrousd };
  }

  async scheduleExpiry() {
    const entries = await this.state.storage.list({ prefix: 'expiry:', limit: 1 });
    const first = entries.values().next().value;
    if (first) await this.state.storage.setAlarm(Math.max(this.now() + 1000, first.expiresAt));
    else await this.state.storage.deleteAlarm();
  }

  async alarm() {
    const now = this.now();
    await this.state.storage.transaction(async tx => {
      const entries = await tx.list({ prefix: 'expiry:', limit: 500 });
      for (const [indexKey, entry] of entries) {
        if (entry.expiresAt > now) break;
        const record = await tx.get(entry.key);
        // Old expiry indexes must never delete a renewed scope counter/lease.
        if (record && record.expiresAt <= now) await tx.delete(entry.key);
        await tx.delete(indexKey);
      }
    });
    await this.scheduleExpiry();
  }
}

export function createGovernanceClient(env, authenticatedContext) {
  requireSecurity(env.LEGEND_GOVERNANCE?.idFromName && env.LEGEND_GOVERNANCE?.get, 'governance_binding_missing', 503);
  const stub = env.LEGEND_GOVERNANCE.get(env.LEGEND_GOVERNANCE.idFromName(`legend-account:${env.LEGEND_ACCOUNT_ID}`));
  async function call(op, context, input) {
    requireSecurity(context === authenticatedContext, 'context_substitution');
    let response;
    try {
      response = await stub.fetch('https://governance.internal/govern', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ op, context, input }),
      });
    } catch { throw new SecurityError('governance_unavailable', 503); }
    let result;
    try { result = await response.json(); } catch { throw new SecurityError('governance_unavailable', 503); }
    if (!response.ok) throw new SecurityError(/^[a-z_]+$/.test(result?.error ?? '') ? result.error : 'governance_unavailable', response.status);
    return result;
  }
  return Object.freeze({
    status: () => call('status', authenticatedContext),
    control: input => call('control', authenticatedContext, input),
    claim: () => call('claim', authenticatedContext),
    close: () => call('close', authenticatedContext),
    reserve: (context, input) => call('reserve', context, input),
    settle: async (context, input) => {
      const result = await call('settle', context, input);
      if (result.overReservation) {
        const error = new SecurityError('provider_cost_exceeded_reservation', 429);
        error.usage = { costMicrousd: result.chargedMicrousd, costEvidence: 'provider_usage' };
        throw error;
      }
      return result;
    },
  });
}
