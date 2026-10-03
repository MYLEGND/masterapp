import test from 'node:test';
import assert from 'node:assert/strict';
import { FOUNDER_BASELINE_MODEL_IDS, FOUNDER_BASELINE_PRIMARY_MODEL, MODEL_REGISTRY } from '../../src/runtime/registry.mjs';
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

function baselineFixture() {
  const f = fixture();
  // Founder conversational baseline defaults to the general route. Specialized
  // roles are selected only by the server-owned task kind, never a request model ID.
  f.envelope.task.kind = 'general';
  f.context.roles = ['Founder']; f.envelope.scope.roles = ['Founder'];
  f.policy = { version: 'legend-founder-baseline.v3', accountId: f.context.accountId,
    tenantId: f.context.tenantId, founderUserId: f.context.userId, serviceKeyId: f.context.keyId,
    requiredRole: 'Founder', environment: 'production', modelIds: FOUNDER_BASELINE_MODEL_IDS,
    lifetimeCostMicrousd: 3000000 };
  f.env.LEGEND_RUNTIME_MODE = 'founder_baseline';
  f.env.LEGEND_DEPLOYMENT_ENVIRONMENT = 'production';
  f.env.LEGEND_FOUNDER_BASELINE_POLICY_JSON = JSON.stringify(f.policy);
  f.env.LEGEND_BUDGET_POLICY_JSON = JSON.stringify({ period: 'lifetime', accountMicrousd: 3000000 });
  return f;
}

test('Founder baseline uses operator-owned five-model router and explicit receipt without fabricating qualification', async () => {
  const f = baselineFixture();
  f.envelope.task.modelId = MODEL_REGISTRY[0].id;
  const result = await orchestrate(f);
  assert.equal(result.status, 'completed');
  assert.equal(result.executionMode, 'founder_baseline');
  assert.deepEqual(f.dispatches, [FOUNDER_BASELINE_PRIMARY_MODEL]);
  assert.equal(f.reservations.length, 1);
  assert.equal(Object.hasOwn(result, 'heldOutPassed'), false);
  assert.equal(Object.hasOwn(result, 'qualificationSuiteSha256'), false);
  assert(MODEL_REGISTRY.every(model => model.enabled === false && model.qualification === null));
});

test('Founder baseline cannot be enabled by request flags or leftover binding in production', async () => {
  for (const mode of [undefined, 'production', 'invalid-mode']) {
    const f = baselineFixture(); f.env.LEGEND_RUNTIME_MODE = mode;
    f.envelope.executionMode = 'founder_baseline'; f.envelope.manualTestPolicy = f.policy;
    const result = await orchestrate(f);
    assert.equal(result.error.code, mode === 'invalid-mode' ? 'runtime_mode_invalid' : 'no_qualified_model');
    assert.equal(f.dispatches.length, 0); assert.equal(f.reservations.length, 0);
  }
});

test('Founder baseline rejects other actors and changed current authenticated scope before spend', async () => {
  for (const changes of [{ userId: 'other-founder' }, { tenantId: 'other-tenant' }, { accountId: 'other-account' },
    { keyId: 'other-service-key' }, { roles: ['LegendQualification'] }, { roles: ['Founder', 'Founder'] },
    { roles: 'Founder' }, { sessionId: 'other-session' }, { conversationId: 'other-thread' },
    { authorizationVersion: 'other-version' }, { requestId: 'other-request' }, { sessionId: '' }]) {
    const f = baselineFixture(); Object.assign(f.context, changes);
    assert.equal((await orchestrate(f)).error.code, 'founder_baseline_scope_denied');
    assert.equal(f.dispatches.length, 0); assert.equal(f.reservations.length, 0);
  }
});

test('Founder baseline requires the exact persistent operator-owned five-model schema', async () => {
  for (const changes of [{ version: 'other' }, { accountId: 'other-account' }, { environment: 'other' },
    { modelIds: MODEL_REGISTRY.slice(0, 4).map(model => model.id) }, { modelIds: [...FOUNDER_BASELINE_MODEL_IDS].reverse() },
    { modelIds: [...FOUNDER_BASELINE_MODEL_IDS, 'external/model'] }, { requiredRole: 'LegendQualification' }, { founderUserId: '' },
    { lifetimeCostMicrousd: 3000001 }, { lifetimeCostMicrousd: 0 },
    { expiresAt: Date.now() + 86400000 }, { unreviewedFlag: true }]) {
    const f = baselineFixture(); f.env.LEGEND_FOUNDER_BASELINE_POLICY_JSON = JSON.stringify({ ...f.policy, ...changes });
    assert.equal((await orchestrate(f)).error.code, 'founder_baseline_configuration_invalid');
    assert.equal(f.dispatches.length, 0); assert.equal(f.reservations.length, 0);
  }
  const f = baselineFixture(); delete f.env.LEGEND_FOUNDER_BASELINE_POLICY_JSON;
  assert.equal((await orchestrate(f)).error.code, 'founder_baseline_configuration_missing');
});

test('Founder baseline preserves lifetime cap and never resets exhausted durable allowance', async () => {
  for (const budget of [{ period: 'calendar-month', accountMicrousd: 3000000 },
    { period: 'lifetime', accountMicrousd: 3000001 }, { period: 'lifetime', accountMicrousd: 0 }]) {
    const f = baselineFixture(); f.env.LEGEND_BUDGET_POLICY_JSON = JSON.stringify(budget);
    assert.equal((await orchestrate(f)).error.code, 'founder_baseline_lifetime_budget_required');
    assert.equal(f.reservations.length, 0); assert.equal(f.dispatches.length, 0);
  }
  const f = baselineFixture(); let calls = 0;
  f.budget.reserve = async () => { calls++; throw Object.assign(new Error(), { name: 'SecurityError', code: 'account_budget_exhausted' }); };
  assert.equal((await orchestrate(f)).error.code, 'account_budget_exhausted');
  assert.equal(calls, 1); assert.equal(f.dispatches.length, 0);
});

test('Founder tools require existing signed callback enablement and existing broker execution', async () => {
  const f = baselineFixture(); f.envelope.task.tools = [{ type: 'function', name: 'calculate' }];
  f.envelope.limits.maxToolCalls = 1;
  assert.equal((await orchestrate(f)).error.code, 'founder_baseline_signed_tools_required');
  assert.equal(f.dispatches.length, 0);
  f.env.LEGEND_TOOL_CALLBACK_ENABLED = 'true';
  let calls = 0;
  f.env.AI.run = async id => { f.dispatches.push(id); return ++calls === 1
    ? { response: '', tool_calls: [{ id: 'call-1', type: 'function', function: { name: 'calculate', arguments: '{"expression":"1+1"}' } }], usage: { prompt_tokens: 20, completion_tokens: 5 } }
    : { response: '2', usage: { prompt_tokens: 20, completion_tokens: 5 } }; };
  const seen = [];
  f.toolBroker = { async execute(call) { seen.push(call); return { output: { result: 2 }, usage: { costMicrousd: 0, costEvidence: 'provider_usage' } }; } };
  const result = await orchestrate(f);
  assert.equal(result.status, 'completed'); assert.equal(result.executionMode, 'founder_baseline');
  assert.equal(seen.length, 1); assert.equal(seen[0].context, f.context); assert.equal(seen[0].call.name, 'calculate');
  assert.deepEqual(f.dispatches, [FOUNDER_BASELINE_PRIMARY_MODEL, FOUNDER_BASELINE_PRIMARY_MODEL]);
});

test('Founder baseline role routing reaches every reviewed model without request-selected model IDs', async () => {
  for (const model of MODEL_REGISTRY) {
    const f = baselineFixture();
    f.envelope.task.kind = model.role;
    // Full-context reservation for GLM/DeepSeek intentionally fits inside the
    // same $3 lifetime account cap while actual usage is settled/refunded.
    f.envelope.limits.maxCostMicrousd = 3000000;
    f.envelope.task.modelId = MODEL_REGISTRY[(MODEL_REGISTRY.indexOf(model) + 1) % MODEL_REGISTRY.length].id;
    const result = await orchestrate(f);
    assert.equal(result.status, 'completed');
    assert.equal(result.provider.modelId, model.id);
    assert.deepEqual(f.dispatches, [model.id]);
  }
});

test('Founder baseline fallback stays inside the exact five-model set and fails closed when all are unavailable', async () => {
  const fallback = baselineFixture();
  fallback.circuit = new CircuitBreaker({ threshold: 1, cooldownMs: 60000 });
  fallback.circuit.failure(FOUNDER_BASELINE_PRIMARY_MODEL);
  const result = await orchestrate(fallback);
  assert.equal(result.status, 'completed');
  assert(FOUNDER_BASELINE_MODEL_IDS.includes(result.provider.modelId));
  assert.notEqual(result.provider.modelId, FOUNDER_BASELINE_PRIMARY_MODEL);

  const blocked = baselineFixture();
  blocked.circuit = new CircuitBreaker({ threshold: 1, cooldownMs: 60000 });
  for (const modelId of FOUNDER_BASELINE_MODEL_IDS) blocked.circuit.failure(modelId);
  assert.equal((await orchestrate(blocked)).error.code, 'no_qualified_model');
  assert.equal(blocked.dispatches.length, 0); assert.equal(blocked.reservations.length, 0);
});
