import test from 'node:test';
import assert from 'node:assert/strict';
import worker from '../../src/index.mjs';
import { FOUNDER_BASELINE_MODEL_IDS, FOUNDER_BASELINE_PRIMARY_MODEL } from '../../src/runtime/registry.mjs';
import { envelope, harness, signedRequest, reserve, NOW } from './fixtures.mjs';

function fixture() {
  const h = harness({
    period: 'lifetime', requestMicrousd: 3000000, userMicrousd: 3000000,
    tenantMicrousd: 3000000, accountMicrousd: 3000000,
    requestConcurrency: 1, userConcurrency: 1, tenantConcurrency: 1, accountConcurrency: 1
  });
  Object.assign(h.env, {
    LEGEND_RUNTIME_MODE: 'founder_baseline',
    LEGEND_DEPLOYMENT_ENVIRONMENT: 'production',
    LEGEND_TOOL_CALLBACK_ENABLED: 'true',
    LEGEND_FOUNDER_BASELINE_POLICY_JSON: JSON.stringify({
      version: 'legend-founder-baseline.v3',
      accountId: 'account-1', tenantId: 'tenant-1', founderUserId: 'user-1',
      serviceKeyId: 'azure-v1', requiredRole: 'Founder', environment: 'production',
      modelIds: FOUNDER_BASELINE_MODEL_IDS, lifetimeCostMicrousd: 3000000
    })
  });
  let modelCalls = 0;
  h.env.AI = { async run() { modelCalls++; return {
    response: 'should not be needed by the control plane',
    usage: { prompt_tokens: 1, completion_tokens: 1 }
  }; } };
  let sequence = 0;
  const body = (extra = {}) => {
    const now = h.now();
    const value = envelope({
      requestId: `founder-console-${++sequence}`,
      issuedAt: now, expiresAt: now + 30000,
      scope: {
        accountId: 'account-1', tenantId: 'tenant-1', userId: 'user-1',
        sessionId: 'founder-session', conversationId: `console-${sequence}`,
        roles: ['Founder'], authorizationVersion: 'console-v1'
      },
      task: { kind: 'general', messages: [{ role: 'user', content: 'Founder control-plane status request.' }],
        tools: [], requiredCapabilities: ['text'] },
      limits: { deadlineUnixMs: now + 30000, maxOutputTokens: 1, maxIterations: 1,
        maxModelCalls: 1, maxToolCalls: 0, maxCostMicrousd: 1 },
      stream: false,
      ...extra
    });
    return value;
  };
  const execution = { waitUntil() {} };
  return { h, body, execution, modelCalls: () => modelCalls };
}

function request(body, path, nonce) {
  return signedRequest(body, { path, nonce: nonce ?? crypto.randomUUID().replaceAll('-', '') });
}

test('Founder status is signed, no-spend, persistent, and returns exact five-model budget state', async () => {
  const f = fixture();
  const response = await worker.fetch(request(f.body(), '/v1/legend/status'), f.h.env, f.execution);
  assert.equal(response.status, 200);
  const result = await response.json();
  assert.equal(result.status, 'ready');
  assert.equal(result.executionMode, 'founder_baseline');
  assert.equal(result.policyVersion, 'legend-founder-baseline.v3');
  assert.equal(result.policyPersistence, 'persistent');
  assert.equal(result.primaryModelId, FOUNDER_BASELINE_PRIMARY_MODEL);
  assert.deepEqual(result.models.map(model => model.id), FOUNDER_BASELINE_MODEL_IDS);
  assert.equal(result.budget.releaseAuthorizedMicrousd, 3000000);
  assert.equal(result.budget.spendCapMicrousd, 3000000);
  assert.equal(result.budget.chargedMicrousd, 0);
  assert.equal(result.budget.remainingMicrousd, 3000000);
  assert.equal(result.budget.paused, false);
  assert.equal(result.budget.concurrencyLimit, 1);
  assert.equal(f.modelCalls(), 0);
  assert.equal([...f.h.storage.data.keys()].some(key => key.startsWith('reservation:')), false);
});

test('Founder control can pause and resume inference without changing the release-authorized maximum', async () => {
  const f = fixture();
  const pausedBody = f.body({ control: { action: 'set_pause', paused: true } });
  const paused = await worker.fetch(request(pausedBody, '/v1/legend/control'), f.h.env, f.execution);
  assert.equal(paused.status, 200);
  const pausedResult = await paused.json();
  assert.equal(pausedResult.budget.paused, true);
  assert.equal(pausedResult.budget.releaseAuthorizedMicrousd, 3000000);

  const blocked = f.body({
    limits: { deadlineUnixMs: f.h.now() + 30000, maxOutputTokens: 64, maxIterations: 1,
      maxModelCalls: 1, maxToolCalls: 0, maxCostMicrousd: 100000 }
  });
  const blockedResponse = await worker.fetch(request(blocked, '/v1/legend/respond'), f.h.env, f.execution);
  const blockedResult = await blockedResponse.json();
  assert.equal(blockedResult.error.code, 'founder_inference_paused');
  assert.equal(f.modelCalls(), 0);

  const resumed = await worker.fetch(request(f.body({ control: { action: 'set_pause', paused: false } }),
    '/v1/legend/control'), f.h.env, f.execution);
  assert.equal(resumed.status, 200);
  assert.equal((await resumed.json()).budget.paused, false);
});

test('Founder spend ceiling is mutable only inside the release-authorized lifetime cap and never below spent budget', async () => {
  const f = fixture();
  const lowered = await worker.fetch(request(f.body({ control: { action: 'set_spend_cap', spendCapMicrousd: 2500000 } }),
    '/v1/legend/control'), f.h.env, f.execution);
  assert.equal(lowered.status, 200);
  assert.equal((await lowered.json()).budget.spendCapMicrousd, 2500000);

  const tooHigh = await worker.fetch(request(f.body({ control: { action: 'set_spend_cap', spendCapMicrousd: 3000001 } }),
    '/v1/legend/control'), f.h.env, f.execution);
  assert.equal(tooHigh.status, 400);
  assert.equal((await tooHigh.json()).error, 'founder_spend_cap_invalid');

  const spendBody = f.body({
    requestId: 'charged-request',
    limits: { deadlineUnixMs: f.h.now() + 30000, maxOutputTokens: 64, maxIterations: 1,
      maxModelCalls: 1, maxToolCalls: 0, maxCostMicrousd: 1000 }
  });
  const session = await f.h.session(spendBody);
  await reserve(session, 'charged-reservation', 1000);
  await session.budget.settle(session.context, {
    reservationId: 'charged-reservation', usageKnown: true, actualCostMicrousd: 1000
  });
  await session.budget.close();

  const belowSpent = await worker.fetch(request(f.body({ control: { action: 'set_spend_cap', spendCapMicrousd: 999 } }),
    '/v1/legend/control'), f.h.env, f.execution);
  assert.equal(belowSpent.status, 400);
  assert.equal((await belowSpent.json()).error, 'founder_spend_cap_invalid');
});

test('control-plane requests reject replay and never accept qualification or non-Founder scope', async () => {
  const f = fixture();
  const body = f.body();
  const nonce = 'b'.repeat(32);
  const first = await worker.fetch(request(body, '/v1/legend/status', nonce), f.h.env, f.execution);
  assert.equal(first.status, 200);
  const replay = await worker.fetch(request(body, '/v1/legend/status', nonce), f.h.env, f.execution);
  assert.equal(replay.status, 409);
  assert.equal((await replay.json()).error, 'request_replayed');

  const wrongRole = f.body();
  wrongRole.scope.roles = ['LegendQualification'];
  const denied = await worker.fetch(request(wrongRole, '/v1/legend/status'), f.h.env, f.execution);
  assert.equal(denied.status, 403);
  assert.equal((await denied.json()).error, 'founder_baseline_scope_denied');
  assert.equal(f.modelCalls(), 0);
});
