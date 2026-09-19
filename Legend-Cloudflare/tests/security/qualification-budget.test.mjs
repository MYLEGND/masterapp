import test from 'node:test';
import assert from 'node:assert/strict';
import { authenticateRequest } from '../../src/security/authenticate.mjs';
import { createGovernanceClient } from '../../src/security/governance.mjs';
import { orchestrate } from '../../src/runtime/orchestrator.mjs';
import { CircuitBreaker } from '../../src/runtime/reliability.mjs';
import { envelope, harness, reserve, signedRequest, NOW } from './fixtures.mjs';

// Independent expected ceilings for full context + 1024 output tokens. These
// tests simulate durable transactions; they perform no inference/cloud calls.
const candidates = [
  ['@cf/qwen/qwen3-30b-a3b-fp8', 2011],
  ['@cf/openai/gpt-oss-120b', 45568],
  ['@cf/zai-org/glm-5.3-flash', 197120],
  ['@cf/zai-org/glm-5.3', 1839514],
  ['@cf/deepseek-ai/deepseek-v4-pro-0813', 1388176],
];

function fixture() {
  const h = harness({ period: 'lifetime', requestMicrousd: 150000,
    userMicrousd: 3000000, tenantMicrousd: 3000000, accountMicrousd: 3000000,
    requestConcurrency: 1, userConcurrency: 1, tenantConcurrency: 1, accountConcurrency: 1 });
  Object.assign(h.env, { LEGEND_RUNTIME_MODE: 'founder_manual_test', LEGEND_DEPLOYMENT_ENVIRONMENT: 'production' });
  const manual = { version: 'legend-founder-manual-test.v1', accountId: 'account-1',
    tenantId: 'founder-tenant', founderUserId: 'founder-user', serviceKeyId: 'founder-key', requiredRole: 'Founder',
    environment: 'production', modelId: candidates[1][0], expiresAt: NOW + 3600000, lifetimeCostMicrousd: 3000000 };
  const policies = candidates.map(([modelId], i) => ({ version: 'legend-qualification.v1', accountId: 'account-1', modelId,
    tenantId: `test-tenant-${i}`, allowedUserIds: [`test-user-${i}`], requiredRole: 'LegendQualification', serviceKeyId: `qual-key-${i}`,
    suiteSha256: String(i + 1).repeat(64), expiresAt: NOW + 3600000, lifetimeCostMicrousd: 3000000 }));
  const keys = Object.fromEntries(['founder-key', 'unlisted-key', ...policies.map(p => p.serviceKeyId)]
    .map((key, i) => [key, Buffer.alloc(32, i + 20).toString('base64')]));
  h.env.LEGEND_MANUAL_TEST_POLICY_JSON = JSON.stringify(manual);
  h.env.LEGEND_SERVICE_KEYS_JSON = JSON.stringify(keys);
  const writePolicies = () => { h.env.LEGEND_QUALIFICATION_POLICIES_JSON = JSON.stringify({
    version: 'legend-qualification-policies.v1', policies }); };
  writePolicies();
  let sequence = 0;
  async function open({ index = 0, founder = false, keyId, change } = {}) {
    const selected = policies[index];
    const scope = { ...envelope().scope, tenantId: founder ? manual.tenantId : selected.tenantId,
      userId: founder ? manual.founderUserId : selected.allowedUserIds[0], roles: [founder ? 'Founder' : 'LegendQualification'] };
    const body = envelope({ requestId: `scoped-request-${++sequence}`, issuedAt: h.now(), expiresAt: h.now() + 120000, scope,
      limits: { ...envelope().limits, deadlineUnixMs: h.now() + 120000, maxOutputTokens: 1024, maxCostMicrousd: 2000000 } });
    if (change) change(body);
    const signingKey = keyId ?? (founder ? manual.serviceKeyId : selected.serviceKeyId);
    const { context } = await authenticateRequest(signedRequest(body, { nonce: crypto.randomUUID().replaceAll('-', ''),
      keyId: signingKey, key: keys[signingKey] }), h.env, { now: h.now });
    const budget = createGovernanceClient(h.env, context);
    await budget.claim();
    return { context, budget, envelope: body };
  }
  return { ...h, manual, policies, writePolicies, open };
}

for (const [index, [modelId, ceiling]] of candidates.entries()) {
  test(`only the exact validated ${modelId} grant receives its request ceiling`, async () => {
    const h = fixture();
    const session = await h.open({ index });
    await assert.rejects(reserve(session, 'oversized', ceiling + 1), { code: 'request_budget_exhausted' });
    assert.equal([...h.storage.data.keys()].filter(key => key.startsWith('quota:')).length, 0);
    assert.equal((await reserve(session, 'bounded', ceiling)).reservedMicrousd, ceiling);
    await assert.rejects(reserve(session, 'additional', 1), { code: 'request_budget_exhausted' });
    const request = [...h.storage.data.entries()].find(([key]) => key.startsWith('request:'))[1];
    assert.equal(request.costLimitMicrousd, ceiling);
    assert.match(request.qualificationBudgetDigest, /^[a-f0-9]{64}$/);
  });
}

test('the real orchestration loop reserves each exact ceiling through the atomic ledger', async t => {
  t.mock.method(Date, 'now', () => NOW);
  for (const [index, [modelId, ceiling]] of candidates.entries()) {
    const h = fixture(); const session = await h.open({ index }); const calls = [];
    h.env.AI = { async run(id) { calls.push(id); return {
      response: 'Synthetic completed output.', usage: { prompt_tokens: 20, completion_tokens: 3 },
    }; } };
    const result = await orchestrate({ ...session, env: h.env, circuit: new CircuitBreaker() });
    assert.equal(result.status, 'completed'); assert.deepEqual(calls, [modelId]);
    const reservation = [...h.storage.data.entries()].find(([key]) => key.startsWith('reservation:'))[1];
    assert.equal(reservation.reservedMicrousd, ceiling);
    assert.equal(reservation.usageKnown, true);
    assert.equal(reservation.chargedMicrousd, result.usage.costMicrousd);
    assert(reservation.chargedMicrousd < ceiling);
  }
});

test('output above the reviewed 1024 ceiling fails as a budget limit before any model call', async t => {
  t.mock.method(Date, 'now', () => NOW);
  for (const [index] of candidates.entries()) {
    const h = fixture();
    const session = await h.open({ index, change: body => { body.limits.maxOutputTokens = 1025; } });
    let calls = 0;
    h.env.AI = { async run() { calls++; throw new Error('Inference must remain unreachable'); } };
    const result = await orchestrate({ ...session, env: h.env, circuit: new CircuitBreaker() });
    assert.equal(result.status, 'failed'); assert.equal(result.error.code, 'request_budget_exhausted');
    assert.equal(calls, 0);
    assert.equal([...h.storage.data.keys()].filter(key => key.startsWith('quota:') || key.startsWith('reservation:')).length, 0);
  }
});

test('ordinary Founder and client model flags cannot raise the 150000 request ceiling', async () => {
  const h = fixture();
  const session = await h.open({ founder: true, change: body => {
    body.runtimeMode = 'qualification'; body.qualification = h.policies[3];
    body.task.modelId = candidates[3][0]; body.maxCostMicrousd = 3000000;
  } });
  await assert.rejects(reserve(session, 'elevated', 150001), { code: 'request_budget_exhausted' });
  assert.equal((await reserve(session, 'normal', 150000)).reservedMicrousd, 150000);
  assert.equal(JSON.parse(h.env.LEGEND_BUDGET_POLICY_JSON).requestMicrousd, 150000);
});

test('request-signed lower budget remains stricter than the qualification ceiling', async () => {
  const h = fixture();
  const session = await h.open({ index: 3, change: body => { body.limits.maxCostMicrousd = 100; } });
  await assert.rejects(reserve(session, 'too-large', 101), { code: 'request_budget_exhausted' });
  await reserve(session, 'bounded', 100);
});

test('valid service signatures cannot substitute qualification actor, role, key or tool permissions', async () => {
  for (const change of [body => { body.scope.tenantId = 'other-tenant'; },
    body => { body.scope.userId = 'other-user'; }, body => { body.scope.roles = ['Founder']; },
    body => { body.scope.roles = ['Founder', 'LegendQualification']; },
    body => { body.task.tools = [{ name: 'legend_capabilities' }]; }]) {
    const h = fixture();
    await assert.rejects(h.open({ index: 3, change }), error =>
      ['qualification_scope_denied', 'qualification_tools_disabled'].includes(error.code));
    assert.equal(h.storage.data.size, 0);
  }
  for (const keyId of ['founder-key', 'unlisted-key']) {
    const h = fixture();
    await assert.rejects(h.open({ index: 3, keyId }), { code: 'qualification_scope_denied' });
    assert.equal(h.storage.data.size, 0);
  }
});

test('other runtime modes and absent grants never enable the elevated qualification ceiling', async () => {
  for (const mode of ['production', 'qualification', 'founder_manual_test']) {
    const h = fixture();
    h.env.LEGEND_RUNTIME_MODE = mode;
    if (mode === 'founder_manual_test') delete h.env.LEGEND_QUALIFICATION_POLICIES_JSON;
    const session = await h.open({ index: 3 });
    await assert.rejects(reserve(session, 'not-enabled', candidates[3][1]), { code: 'request_budget_exhausted' });
  }
});

test('malformed, over-budget and overlapping operator grants fail before durable admission', async () => {
  for (const mutate of [h => { h.policies[3].serviceKeyId = h.manual.serviceKeyId; },
    h => { h.policies[3].tenantId = h.manual.tenantId; }, h => { h.policies[3].allowedUserIds = [h.manual.founderUserId]; },
    h => { h.policies[3].suiteSha256 = ''; }, h => { h.policies[3].lifetimeCostMicrousd = 3000001; },
    h => { h.policies[3].lifetimeCostMicrousd = 2999999; }, h => { h.policies[3].expiresAt = NOW - 1; }]) {
    const h = fixture(); mutate(h); h.writePolicies();
    await assert.rejects(h.open({ index: 3 }));
    assert.equal(h.storage.data.size, 0);
  }
});

test('an expired unrelated grant leaves Founder and active qualification admission available', async () => {
  const h = fixture(); h.policies[4].expiresAt = NOW - 1; h.writePolicies();
  const founder = await h.open({ founder: true }); await reserve(founder, 'normal', 100);
  await founder.budget.settle(founder.context, { reservationId: 'normal', usageKnown: true, actualCostMicrousd: 100 });
  await reserve(await h.open({ index: 0 }), 'active-qualification', candidates[0][1]);
  await assert.rejects(h.open({ index: 4 }), { code: 'qualification_configuration_invalid' });
});

test('removal or alteration of a grant cannot upgrade an already claimed request', async () => {
  for (const change of ['remove', 'suite', 'allowed-user-set', 'expiry']) {
    const h = fixture(); const session = await h.open({ index: 0 });
    if (change === 'remove') delete h.env.LEGEND_QUALIFICATION_POLICIES_JSON;
    else {
      if (change === 'suite') h.policies[0].suiteSha256 = 'f'.repeat(64);
      if (change === 'allowed-user-set') h.policies[0].allowedUserIds.push('another-test-user');
      if (change === 'expiry') h.policies[0].expiresAt += 1000;
      h.writePolicies();
    }
    await assert.rejects(reserve(session, 'changed', 1), { code: 'qualification_budget_policy_changed' });
    assert.equal([...h.storage.data.keys()].filter(key => key.startsWith('quota:')).length, 0);
  }
});

test('account concurrency remains one across Founder and qualification requests', async () => {
  const h = fixture(); const qualification = await h.open(); const founder = await h.open({ founder: true });
  await reserve(qualification, 'qualification', candidates[0][1]);
  await assert.rejects(reserve(founder, 'founder', 100), { code: 'account_concurrency_exhausted' });
  await qualification.budget.settle(qualification.context,
    { reservationId: 'qualification', usageKnown: true, actualCostMicrousd: 100 });
  await reserve(founder, 'founder', 100);
});

test('prior unknown debits and new candidates share the unchanged 3000000 lifetime ledger', async () => {
  const h = fixture(); const prior = await h.open({ founder: true });
  await reserve(prior, 'prior', 57711);
  await prior.budget.settle(prior.context, { reservationId: 'prior', usageKnown: false });
  await prior.budget.close(); h.advance(120001); h.restart();
  const glm = await h.open({ index: 3 }); await reserve(glm, 'glm', candidates[3][1]);
  await glm.budget.settle(glm.context, { reservationId: 'glm', usageKnown: false });
  await glm.budget.close(); h.advance(120001); h.restart();
  const deepseek = await h.open({ index: 4 });
  await assert.rejects(reserve(deepseek, 'deepseek', candidates[4][1]), { code: 'account_budget_exhausted' });
  const quotas = [...h.storage.data.entries()].filter(([key]) => key.startsWith('quota:lifetime:'));
  assert.equal(Math.max(...quotas.map(([, value]) => value.chargedMicrousd)), 57711 + candidates[3][1]);
  assert(quotas.every(([, value]) => value.expiresAt === null));
  assert.equal(JSON.parse(h.env.LEGEND_BUDGET_POLICY_JSON).accountMicrousd, 3000000);
});

test('grant revocation or expiry cannot block settlement and close of dispatched work', async () => {
  for (const usageKnown of [false, true]) {
    for (const revoked of [false, true]) {
      const h = fixture(); const session = await h.open({ index: 3 });
      await reserve(session, 'dispatched', candidates[3][1]);
      if (revoked) delete h.env.LEGEND_QUALIFICATION_POLICIES_JSON;
      else { h.policies[3].expiresAt = NOW - 1; h.writePolicies(); }
      assert.equal((await session.budget.settle(session.context,
        { reservationId: 'dispatched', usageKnown, actualCostMicrousd: 99 })).chargedMicrousd,
      usageKnown ? 99 : candidates[3][1]);
      assert.deepEqual(await session.budget.close(), { closed: true });
      const active = [...h.storage.data.entries()].filter(([key]) => key.startsWith('leases:'));
      assert(active.every(([, value]) => value.active.length === (usageKnown ? 0 : 1)));
    }
  }
});
