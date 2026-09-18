import test from 'node:test';
import assert from 'node:assert/strict';
import { MODEL_REGISTRY } from '../../src/runtime/registry.mjs';
import { orchestrate } from '../../src/runtime/orchestrator.mjs';
import { CircuitBreaker } from '../../src/runtime/reliability.mjs';

// SIMULATION ONLY. No credentials, real provider calls, or passing qualifications.
function fixture() {
  const now = Date.now();
  const scope = { accountId: 'qualification-account', tenantId: 'test-tenant', userId: 'test-user', sessionId: 'test-session', conversationId: 'test-conversation', roles: ['LegendQualification'], authorizationVersion: '1' };
  const envelope = { version: 'legend-cloudflare.v1', requestId: 'qualification-request', issuedAt: now, expiresAt: now + 10000, scope,
    task: { kind: 'architecture', messages: [{ role: 'user', content: 'Compute the requested result.' }], tools: [], requiredCapabilities: [] },
    limits: { deadlineUnixMs: now + 10000, maxOutputTokens: 128, maxIterations: 2, maxModelCalls: 2, maxToolCalls: 0, maxCostMicrousd: 1000000 }, stream: false };
  const context = { ...scope, requestId: envelope.requestId, keyId: 'qualification-key' };
  const policy = { version: 'legend-qualification.v1', accountId: scope.accountId, modelId: MODEL_REGISTRY[0].id,
    tenantId: scope.tenantId, allowedUserIds: [scope.userId], requiredRole: 'LegendQualification', serviceKeyId: context.keyId,
    suiteSha256: '1'.repeat(64), expiresAt: now + 60000, lifetimeCostMicrousd: 5000000 };
  const dispatches = []; const reservations = [];
  const env = { LEGEND_RUNTIME_MODE: 'qualification', LEGEND_DEPLOYMENT_ENVIRONMENT: 'qualification', LEGEND_ACCOUNT_ID: scope.accountId,
    LEGEND_QUALIFICATION_POLICY_JSON: JSON.stringify(policy), LEGEND_BUDGET_POLICY_JSON: JSON.stringify({ period: 'lifetime', accountMicrousd: 5000000 }),
    AI: { async run(modelId) { dispatches.push(modelId); return { response: 'fixture output', usage: { prompt_tokens: 20, completion_tokens: 5 } }; } } };
  const budget = { async reserve(context, receipt) { reservations.push(receipt); return { reservationId: receipt.reservationId, reservedMicrousd: receipt.maxCostMicrousd }; },
    async settle(context, receipt) { return { chargedMicrousd: receipt.usageKnown ? receipt.actualCostMicrousd : reservations.at(-1).maxCostMicrousd }; } };
  return { envelope, context, env, budget, circuit: new CircuitBreaker(), policy, dispatches, reservations };
}

test('bounded operator qualification uses existing loop without fabricating passing flags', async () => {
  const f = fixture(); const result = await orchestrate(f);
  assert.equal(result.status, 'completed'); assert.equal(result.executionMode, 'qualification');
  assert.equal(result.qualificationSuiteSha256, f.policy.suiteSha256);
  assert.deepEqual(f.dispatches, [MODEL_REGISTRY[0].id]); assert.equal(f.reservations.length, 1);
  assert(MODEL_REGISTRY.every(model => model.enabled === false && model.qualification === null));
  assert.equal(Object.hasOwn(result, 'heldOutPassed'), false);
});

test('production cannot opt into qualification through body or leftover env policy', async () => {
  for (const mode of [undefined, 'production']) {
    const f = fixture(); f.env.LEGEND_RUNTIME_MODE = mode;
    f.envelope.runtimeMode = 'qualification'; f.envelope.qualification = f.policy;
    f.env.LEGEND_QUALIFICATION_POLICY_JSON = '{invalid leftover config';
    const result = await orchestrate(f);
    assert.equal(result.error.code, 'no_qualified_model'); assert.equal(f.dispatches.length, 0); assert.equal(f.reservations.length, 0);
  }
});

test('qualification requires dedicated deployment environment and valid runtime mode', async () => {
  for (const overrides of [{ LEGEND_DEPLOYMENT_ENVIRONMENT: 'production' }, { LEGEND_RUNTIME_MODE: 'preview' }]) {
    const f = fixture(); Object.assign(f.env, overrides);
    assert.equal((await orchestrate(f)).error.code, 'runtime_mode_invalid'); assert.equal(f.dispatches.length, 0);
  }
});

test('qualification rejects substituted user, tenant, account, signing key and missing test role before spend', async () => {
  for (const changes of [{ userId: 'other-user' }, { tenantId: 'other-tenant' }, { accountId: 'other-account' }, { keyId: 'production-key' }, { roles: ['Founder'] }, { roles: 'LegendQualification' }, { requestId: 'other-request' }]) {
    const f = fixture(); Object.assign(f.context, changes);
    assert.equal((await orchestrate(f)).error.code, 'qualification_scope_denied'); assert.equal(f.reservations.length, 0); assert.equal(f.dispatches.length, 0);
  }
});

test('qualification requires positive lifetime account budget within operator cap', async () => {
  for (const budgetPolicy of [{ period: 'calendar-month', accountMicrousd: 1 }, { period: 'lifetime', accountMicrousd: 0 }, { period: 'lifetime', accountMicrousd: 5000001 }]) {
    const f = fixture(); f.env.LEGEND_BUDGET_POLICY_JSON = JSON.stringify(budgetPolicy);
    assert.equal((await orchestrate(f)).error.code, 'qualification_lifetime_budget_required'); assert.equal(f.reservations.length, 0);
  }
});

test('qualification configuration is expiring, account matched and selects only catalog candidates', async () => {
  for (const changes of [{ expiresAt: Date.now() - 1 }, { expiresAt: Date.now() + 2 * 86400000 }, { accountId: 'different-account' }, { modelId: 'https://external.example/model' }, { suiteSha256: '' }, { allowedUserIds: [] }, { requiredRole: 'Founder' }, { lifetimeCostMicrousd: Number.MAX_SAFE_INTEGER }]) {
    const f = fixture(); f.env.LEGEND_QUALIFICATION_POLICY_JSON = JSON.stringify({ ...f.policy, ...changes });
    assert.equal((await orchestrate(f)).error.code, 'qualification_configuration_invalid'); assert.equal(f.dispatches.length, 0);
  }
});

test('model and suite are fixed by operator config and never selected by request fields', async () => {
  const f = fixture(); f.envelope.task.modelId = MODEL_REGISTRY[4].id; f.envelope.qualificationSuiteSha256 = 'f'.repeat(64);
  f.env.LEGEND_QUALIFICATION_POLICY_JSON = JSON.stringify({ ...f.policy, modelId: MODEL_REGISTRY[1].id });
  const result = await orchestrate(f);
  assert.equal(result.provider.modelId, MODEL_REGISTRY[1].id); assert.equal(result.qualificationSuiteSha256, f.policy.suiteSha256);
});

test('qualification deadline cannot outlive operator grant and tools stay disabled', async () => {
  const deadline = fixture(); deadline.env.LEGEND_QUALIFICATION_POLICY_JSON = JSON.stringify({ ...deadline.policy, expiresAt: Date.now() + 500 });
  assert.equal((await orchestrate(deadline)).error.code, 'qualification_deadline_exceeded');
  const tools = fixture(); tools.envelope.task.tools = [{ type: 'function', name: 'execute_code' }];
  assert.equal((await orchestrate(tools)).error.code, 'qualification_tools_disabled'); assert.equal(tools.reservations.length, 0);
});

test('qualification cannot bypass an exhausted durable budget when candidate changes', async () => {
  const f = fixture(); let reserves = 0;
  f.budget.reserve = async () => { reserves++; throw Object.assign(new Error(), { name: 'SecurityError', code: 'account_budget_exhausted' }); };
  for (const model of MODEL_REGISTRY.slice(0, 2)) {
    f.env.LEGEND_QUALIFICATION_POLICY_JSON = JSON.stringify({ ...f.policy, modelId: model.id });
    assert.equal((await orchestrate(f)).error.code, 'account_budget_exhausted');
  }
  assert.equal(reserves, 2); assert.equal(f.dispatches.length, 0);
});
