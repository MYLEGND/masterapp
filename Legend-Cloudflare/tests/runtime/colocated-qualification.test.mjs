import test from 'node:test';
import assert from 'node:assert/strict';
import { MODEL_REGISTRY, estimateCostMicrousd } from '../../src/runtime/registry.mjs';
import { orchestrate } from '../../src/runtime/orchestrator.mjs';
import { CircuitBreaker } from '../../src/runtime/reliability.mjs';

// SIMULATION ONLY: fake provider/ledger, no credentials or actual acceptance.
function fixture() {
  const now = Date.now();
  const scope = { accountId: 'account', tenantId: 'founder-tenant', userId: 'founder',
    sessionId: 'session', conversationId: 'conversation', roles: ['Founder'], authorizationVersion: 'v1' };
  const envelope = { version: 'legend-cloudflare.v1', requestId: 'request', issuedAt: now, expiresAt: now + 10000, scope,
    task: { kind: 'general', messages: [{ role: 'user', content: 'Synthetic fixture.' }], tools: [], requiredCapabilities: [] },
    limits: { deadlineUnixMs: now + 10000, maxOutputTokens: 1024, maxIterations: 1, maxModelCalls: 1, maxToolCalls: 0, maxCostMicrousd: 3000000 }, stream: false };
  const context = { ...scope, keyId: 'founder-key', requestId: envelope.requestId };
  const manual = { version: 'legend-founder-manual-test.v1', accountId: scope.accountId,
    tenantId: scope.tenantId, founderUserId: scope.userId, serviceKeyId: context.keyId, requiredRole: 'Founder',
    environment: 'production', modelId: '@cf/openai/gpt-oss-120b', expiresAt: now + 60000, lifetimeCostMicrousd: 3000000 };
  const policies = MODEL_REGISTRY.map((model, i) => ({ version: 'legend-qualification.v1', accountId: scope.accountId,
    modelId: model.id, tenantId: 'test-tenant', allowedUserIds: ['test-user-' + i], requiredRole: 'LegendQualification',
    serviceKeyId: 'test-key-' + i, suiteSha256: 'a'.repeat(64), expiresAt: now + 60000, lifetimeCostMicrousd: 3000000 }));
  const reservations = []; const calls = []; const settled = [];
  const env = { LEGEND_RUNTIME_MODE: 'founder_manual_test', LEGEND_DEPLOYMENT_ENVIRONMENT: 'production', LEGEND_ACCOUNT_ID: scope.accountId,
    LEGEND_MANUAL_TEST_POLICY_JSON: JSON.stringify(manual),
    LEGEND_BUDGET_POLICY_JSON: JSON.stringify({ period: 'lifetime', accountMicrousd: 3000000 }),
    AI: { async run(modelId, input) { calls.push({ modelId, input }); return { response: 'Synthetic result', usage: { prompt_tokens: 20, completion_tokens: 5 } }; } } };
  const budget = { async reserve(ctx, request) { reservations.push({ ctx, ...request }); return { reservationId: request.reservationId, reservedMicrousd: request.maxCostMicrousd }; },
    async settle(ctx, request) { settled.push(request); return { chargedMicrousd: request.usageKnown ? request.actualCostMicrousd : reservations.at(-1).maxCostMicrousd }; } };
  const f = { envelope, context, env, budget, circuit: new CircuitBreaker(), manual, policies, reservations, calls, settled };
  f.configure = () => { env.LEGEND_QUALIFICATION_POLICIES_JSON = JSON.stringify({ version: 'legend-qualification-policies.v1', policies }); };
  f.select = index => {
    const policy = policies[index];
    Object.assign(scope, { tenantId: policy.tenantId, userId: policy.allowedUserIds[0], roles: ['LegendQualification'] });
    Object.assign(context, scope, { keyId: policy.serviceKeyId });
  };
  return f;
}

test('default Founder baseline stays fixed GPT with no list and with a valid operator list', async () => {
  for (const configured of [false, true]) {
    const f = fixture(); if (configured) f.configure();
    f.envelope.task.modelId = MODEL_REGISTRY[4].id;
    f.envelope.qualificationSuiteSha256 = 'f'.repeat(64);
    const result = await orchestrate(f);
    assert.equal(result.status, 'completed'); assert.equal(result.executionMode, 'founder_manual_test');
    assert.equal(result.provider.modelId, f.manual.modelId);
    assert.equal(Object.hasOwn(result, 'qualificationSuiteSha256'), false);
  }
});

test('each distinct authenticated qualification key uses its exact operator model and suite in the same loop', async () => {
  for (let i = 0; i < MODEL_REGISTRY.length; i++) {
    const f = fixture(); f.configure(); f.select(i);
    f.envelope.task.modelId = MODEL_REGISTRY[(i + 1) % MODEL_REGISTRY.length].id;
    f.envelope.qualificationSuiteSha256 = 'f'.repeat(64);
    const result = await orchestrate(f);
    assert.equal(result.status, 'completed'); assert.equal(result.executionMode, 'qualification');
    assert.equal(result.provider.modelId, f.policies[i].modelId);
    assert.equal(result.qualificationSuiteSha256, f.policies[i].suiteSha256);
    assert.equal(f.calls.length, 1); assert.equal(f.reservations.length, 1);
    assert.equal(f.reservations[0].ctx.accountId, f.manual.accountId);
    assert.equal(f.reservations[0].maxCostMicrousd, estimateCostMicrousd(MODEL_REGISTRY[i], MODEL_REGISTRY[i].contextTokens, 1024));
    assert.equal(f.calls[0].input[MODEL_REGISTRY[i].outputLimitParameter], 1024);
    assert(MODEL_REGISTRY.every(model => model.enabled === false && model.qualification === null));
  }
});

test('qualification role with manual or unlisted key cannot fall through to Founder access', async () => {
  for (const keyId of ['founder-key', 'unknown-key']) {
    const f = fixture(); f.configure(); f.select(0); f.context.keyId = keyId;
    const result = await orchestrate(f);
    assert.equal(result.error.code, 'qualification_scope_denied'); assert.equal(f.calls.length, 0); assert.equal(f.reservations.length, 0);
  }
});

test('listed qualification key rejects wrong scope, mixed roles and changed authorization version', async () => {
  for (const change of [{ tenantId: 'founder-tenant' }, { userId: 'founder' }, { accountId: 'foreign-account' },
    { roles: ['Founder'] }, { roles: ['LegendQualification', 'Founder'] }, { roles: [] },
    { roles: ['LegendQualification', 'LegendQualification'] }, { authorizationVersion: 'other' },
    { sessionId: '' }, { conversationId: 'other' }, { requestId: 'other' }]) {
    const f = fixture(); f.configure(); f.select(0); Object.assign(f.context, change);
    const result = await orchestrate(f);
    assert.equal(result.error.code, 'qualification_scope_denied'); assert.equal(f.reservations.length, 0);
  }
  const f = fixture(); f.configure(); f.select(0); f.envelope.scope.roles = ['Founder'];
  assert.equal((await orchestrate(f)).error.code, 'qualification_scope_denied');
});

test('any malformed, duplicate, shared Founder identity or broader-cap entry blocks dispatch', async () => {
  for (const change of [{ serviceKeyId: 'founder-key' }, { tenantId: 'founder-tenant' }, { allowedUserIds: ['founder'] },
    { serviceKeyId: 'test-key-0' }, { modelId: MODEL_REGISTRY[0].id }, { suiteSha256: '' }, { suiteSha256: 'F'.repeat(64) },
    { modelId: 'external/other-model' }, { expiresAt: 0 }, { accountId: 'foreign-account' },
    { requiredRole: 'Founder' }, { lifetimeCostMicrousd: 3000001 }, { extraOverride: true }]) {
    const f = fixture(); Object.assign(f.policies[4], change); f.configure(); f.select(0);
    const result = await orchestrate(f);
    assert.equal(result.error.code, 'qualification_configuration_invalid', JSON.stringify(change));
    assert.equal(f.reservations.length, 0); assert.equal(f.calls.length, 0);
  }
});

test('an expired unrelated qualification policy leaves Founder baseline and other candidates available', async () => {
  for (const selected of [-1, 0]) {
    const f = fixture(); f.policies[4].expiresAt = Date.now() - 1; f.configure();
    if (selected >= 0) f.select(selected);
    const result = await orchestrate(f);
    assert.equal(result.status, 'completed');
    assert.equal(result.provider.modelId, selected < 0 ? f.manual.modelId : f.policies[selected].modelId);
  }
  const expired = fixture(); expired.policies[4].expiresAt = Date.now() - 1; expired.configure(); expired.select(4);
  assert.equal((await orchestrate(expired)).error.code, 'qualification_configuration_invalid');
  assert.equal(expired.reservations.length, 0);
});

test('invalid list or incompatible lifetime budget cannot create an alternate ledger allowance', async () => {
  for (const config of [null, {}, { version: 'legend-qualification-policies.v1', policies: [] },
    { version: 'other', policies: [fixture().policies[0]] },
    { version: 'legend-qualification-policies.v1', policies: fixture().policies, extra: true }]) {
    const f = fixture(); f.env.LEGEND_QUALIFICATION_POLICIES_JSON = JSON.stringify(config);
    assert.equal((await orchestrate(f)).error.code, 'qualification_configuration_invalid');
    assert.equal(f.reservations.length, 0);
  }
  const f = fixture(); f.configure(); f.select(0);
  f.env.LEGEND_BUDGET_POLICY_JSON = JSON.stringify({ period: 'calendar-month', accountMicrousd: 3000000 });
  assert.equal((await orchestrate(f)).error.code, 'manual_test_lifetime_budget_required');
});

test('production ignores policy lists and request mode flags; absent list grants no qualification', async () => {
  for (const mode of [undefined, 'production']) {
    const f = fixture(); f.configure(); f.select(0); f.env.LEGEND_RUNTIME_MODE = mode;
    f.envelope.runtimeMode = 'qualification'; f.envelope.qualification = f.policies[0];
    assert.equal((await orchestrate(f)).error.code, 'no_qualified_model'); assert.equal(f.calls.length, 0);
  }
  const f = fixture(); f.select(0);
  assert.equal((await orchestrate(f)).error.code, 'manual_test_scope_denied');
});

test('qualification keeps tools disabled and grant deadline enforced', async () => {
  const tools = fixture(); tools.configure(); tools.select(0);
  tools.envelope.task.tools = [{ type: 'function', name: 'mutate' }];
  assert.equal((await orchestrate(tools)).error.code, 'qualification_tools_disabled'); assert.equal(tools.reservations.length, 0);
  const late = fixture(); late.policies[0].expiresAt = Date.now() + 1000; late.configure(); late.select(0);
  assert.equal((await orchestrate(late)).error.code, 'qualification_deadline_exceeded'); assert.equal(late.reservations.length, 0);
});

test('ordinary 150k request cap still rejects large-context candidates before reserve without reducing reasoning', async () => {
  for (const i of [2, 3, 4]) {
    const f = fixture(); f.configure(); f.select(i); f.envelope.limits.maxCostMicrousd = 150000;
    const result = await orchestrate(f);
    assert.equal(result.error.code, 'request_budget_exhausted'); assert.equal(f.calls.length, 0); assert.equal(f.reservations.length, 0);
    assert.equal(result.modelSettings.reasoningEffort, 'high');
  }
});

test('every model and Founder request still goes through the same injected account budget authority', async () => {
  let attempts = 0;
  const sharedBudget = { async reserve() { attempts++; throw Object.assign(new Error(), { name: 'SecurityError', code: 'account_budget_exhausted' }); }, async settle() { throw Error('not reserved'); } };
  for (let i = -1; i < MODEL_REGISTRY.length; i++) {
    const f = fixture(); f.configure(); if (i >= 0) f.select(i); f.budget = sharedBudget;
    assert.equal((await orchestrate(f)).error.code, 'account_budget_exhausted'); assert.equal(f.calls.length, 0);
  }
  assert.equal(attempts, 6);
});

test('unknown provider usage retains the full reservation with no fabricated qualification', async () => {
  const f = fixture(); f.configure(); f.select(3);
  f.env.AI.run = async () => { throw new Error('synthetic lost response'); };
  const result = await orchestrate(f);
  assert.equal(result.status, 'failed'); assert.equal(result.usage.costEvidence, 'reserved_upper_bound');
  assert.equal(result.usage.costMicrousd, f.reservations[0].maxCostMicrousd);
  assert.equal(f.settled[0].usageKnown, false); assert.equal(Object.hasOwn(result, 'heldOutPassed'), false);
});
