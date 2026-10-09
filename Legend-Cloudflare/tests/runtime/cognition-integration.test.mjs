import test from 'node:test';
import assert from 'node:assert/strict';
import worker from '../../src/index.mjs';
import { FOUNDER_BASELINE_MODEL_IDS } from '../../src/runtime/registry.mjs';
import { envelope, harness, signedRequest, NOW } from '../security/fixtures.mjs';

const GPT = '@cf/openai/gpt-oss-120b';
const ARCHITECT = '@cf/zai-org/glm-5.3';
const REASONER = '@cf/deepseek-ai/deepseek-v4-pro-0813';

function providerResponse(text) {
  return {
    choices: [{ message: { content: text }, finish_reason: 'stop' }],
    usage: { prompt_tokens: 20, completion_tokens: 10 }
  };
}

function fixture() {
  const h = harness({
    period: 'lifetime',
    requestMicrousd: 3000000,
    userMicrousd: 3000000,
    tenantMicrousd: 3000000,
    accountMicrousd: 3000000,
    requestConcurrency: 1,
    userConcurrency: 1,
    tenantConcurrency: 1,
    accountConcurrency: 1,
  });
  h.advance(Date.now() - NOW);
  Object.assign(h.env, {
    LEGEND_RUNTIME_MODE: 'founder_baseline',
    LEGEND_DEPLOYMENT_ENVIRONMENT: 'production',
    LEGEND_TOOL_CALLBACK_ENABLED: 'true',
    LEGEND_FOUNDER_BASELINE_POLICY_JSON: JSON.stringify({
      version: 'legend-founder-baseline.v3',
      accountId: 'account-1',
      tenantId: 'tenant-1',
      founderUserId: 'user-1',
      serviceKeyId: 'azure-v1',
      requiredRole: 'Founder',
      environment: 'production',
      modelIds: FOUNDER_BASELINE_MODEL_IDS,
      lifetimeCostMicrousd: 3000000,
    }),
  });
  const calls = [];
  h.env.AI = {
    async run(id, input) {
      calls.push({ id, input });
      if (calls.length === 1) return providerResponse(JSON.stringify({
        version: 'legend-cognition-plan.v1',
        complexity: 4,
        specialists: ['architecture'],
        verification: true,
      }));
      if (id === ARCHITECT) return providerResponse('Architecture finding: preserve the canonical runtime and change only its executive policy.');
      if (id === REASONER) return providerResponse('Independent critique: require evidence before treating specialist claims as verified.');
      if (id === GPT) return providerResponse('One synthesized LEGEND answer.');
      throw new Error('unexpected model');
    }
  };
  return { h, calls };
}

test('Founder adaptive cognition delegates to distinct specialists then returns one GPT-OSS synthesis', async () => {
  const f = fixture();
  const now = f.h.now();
  const body = envelope({
    requestId: 'founder-cognition-1',
    issuedAt: now,
    expiresAt: now + 60000,
    scope: {
      accountId: 'account-1',
      tenantId: 'tenant-1',
      userId: 'user-1',
      sessionId: 'founder-session',
      conversationId: 'founder-cognition',
      roles: ['Founder'],
      authorizationVersion: 'cognition-v1',
    },
    task: {
      kind: 'general',
      messages: [{ role: 'user', content: 'Audit the architecture and challenge the reasoning before answering.' }],
      tools: [],
      requiredCapabilities: ['text'],
      cognition: {
        version: 'legend-cognition.v1',
        mode: 'adaptive',
        maxSpecialists: 2,
        independentCritique: true,
      },
    },
    limits: {
      deadlineUnixMs: now + 60000,
      maxOutputTokens: 128,
      maxIterations: 2,
      maxModelCalls: 5,
      maxToolCalls: 0,
      maxCostMicrousd: 3000000,
    },
    stream: false,
  });
  const response = await worker.fetch(signedRequest(body), f.h.env, { waitUntil() {} });
  assert.equal(response.status, 200);
  const result = await response.json();
  assert.equal(result.status, 'completed');
  assert.equal(result.text, 'One synthesized LEGEND answer.');
  assert.equal(result.cognition.planStatus, 'planned');
  assert.deepEqual(result.cognition.specialistRoles, ['architecture', 'reasoning']);
  assert.equal(result.cognition.verification, true);
  assert.deepEqual(f.calls.map(call => call.id), [GPT, ARCHITECT, REASONER, GPT]);
  assert.deepEqual(result.cognition.calls.map(call => call.modelId), [GPT, ARCHITECT, REASONER, GPT]);
  assert.equal(result.provider.modelId, GPT);
});

test('invalid planner output cannot select a specialist and falls back transparently to direct synthesis', async () => {
  const f = fixture();
  f.h.env.AI.run = async (id, input) => {
    f.calls.push({ id, input });
    return f.calls.length === 1
      ? providerResponse('not-json')
      : providerResponse('Direct LEGEND answer after invalid plan.');
  };
  const now = f.h.now();
  const body = envelope({
    requestId: 'founder-cognition-invalid-plan',
    issuedAt: now,
    expiresAt: now + 60000,
    scope: {
      accountId: 'account-1', tenantId: 'tenant-1', userId: 'user-1',
      sessionId: 'founder-session', conversationId: 'founder-cognition-invalid',
      roles: ['Founder'], authorizationVersion: 'cognition-v1',
    },
    task: {
      kind: 'general',
      messages: [{ role: 'user', content: 'Answer this normally.' }],
      tools: [],
      requiredCapabilities: ['text'],
      cognition: {
        version: 'legend-cognition.v1',
        mode: 'adaptive',
        maxSpecialists: 2,
        independentCritique: true,
      },
    },
    limits: {
      deadlineUnixMs: now + 60000,
      maxOutputTokens: 128,
      maxIterations: 2,
      maxModelCalls: 5,
      maxToolCalls: 0,
      maxCostMicrousd: 3000000,
    },
    stream: false,
  });
  const response = await worker.fetch(signedRequest(body), f.h.env, { waitUntil() {} });
  const result = await response.json();
  assert.equal(result.status, 'completed');
  assert.equal(result.cognition.planStatus, 'invalid_direct_fallback');
  assert.deepEqual(result.cognition.specialistRoles, []);
  assert.deepEqual(f.calls.map(call => call.id), [GPT, GPT]);
});
