import test from 'node:test';
import assert from 'node:assert/strict';
import { MODEL_REGISTRY, estimateCostMicrousd, routeModel } from '../../src/runtime/registry.mjs';
import { buildProviderInput, parseProviderResponse } from '../../src/runtime/adapter.mjs';
import { orchestrate, createEventStream } from '../../src/runtime/orchestrator.mjs';
import { CircuitBreaker } from '../../src/runtime/reliability.mjs';

// SIMULATION ONLY: injected binding, qualifications and budget below are not live Cloudflare evidence.
function fixture(overrides = {}) {
  const now = Date.now();
  const envelope = { version: 'legend-cloudflare.v1', requestId: 'simulation-request-1', issuedAt: now, expiresAt: now + 10000,
    scope: { accountId: 'simulation', tenantId: 'tenant-a', userId: 'user-a', sessionId: 'session-a', conversationId: 'conversation-a', roles: [], authorizationVersion: '1' },
    task: { kind: 'efficient', messages: [{ role: 'user', content: 'Compute 6 * 7.' }], tools: [], requiredCapabilities: [] },
    limits: { deadlineUnixMs: now + 10000, maxOutputTokens: 128, maxIterations: 3, maxModelCalls: 3, maxToolCalls: 4, maxCostMicrousd: 20000 }, stream: false };
  const reservations = new Map(); const settlements = []; const dispatches = [];
  const registry = MODEL_REGISTRY.map(model => ({ ...model, enabled: true, qualification: { accountId: 'simulation', accountCanaryPassed: true, heldOutPassed: true, expiresAt: now + 10000 } }));
  const budget = {
    async reserve(context, value) { assert(!reservations.has(value.reservationId)); reservations.set(value.reservationId, value); return { reservationId: value.reservationId, reservedMicrousd: value.maxCostMicrousd }; },
    async settle(context, value) { settlements.push(value); return { chargedMicrousd: value.usageKnown ? value.actualCostMicrousd : reservations.get(value.reservationId).maxCostMicrousd, usageKnown: value.usageKnown }; },
  };
  const env = { AI: { async run(id, input) { dispatches.push({ id, input }); return response('42'); } } };
  return { envelope, context: { scope: envelope.scope }, env, budget, registry, circuit: new CircuitBreaker(), reservations, settlements, dispatches, ...overrides };
}
function response(text, calls) { return { choices: [{ message: { content: text, tool_calls: calls }, finish_reason: 'stop' }], usage: { prompt_tokens: 20, completion_tokens: 5 } }; }
function call(id = 'call-1', name = 'read_repository') { return { id, type: 'function', function: { name, arguments: '{"path":"README.md"}' } }; }

test('shipping registry is entirely disabled and cannot spend', async () => {
  const f = fixture({ registry: MODEL_REGISTRY });
  const result = await orchestrate(f);
  assert.equal(result.error.code, 'no_qualified_model'); assert.equal(f.dispatches.length, 0); assert.equal(f.reservations.size, 0);
});
test('simulation success preserves hosted provider identity and accounts actual usage', async () => {
  const f = fixture(); const result = await orchestrate(f);
  assert.equal(result.status, 'completed'); assert.equal(result.text, '42');
  assert.deepEqual(result.provider, { name: 'cloudflare-workers-ai', modelId: MODEL_REGISTRY[0].id, hosting: 'cloudflare' });
  assert.equal(result.usage.costMicrousd, 3); assert.equal(result.usage.costEvidence, 'provider_usage');
  assert.equal(f.dispatches[0].input.max_tokens, 128); assert.equal(f.dispatches[0].input.stream, false);
});
test('expired qualification, wrong host and unmet capability cannot route', () => {
  const f = fixture(); const args = { task: f.envelope.task, accountId: 'simulation', inputTokens: 100, maxOutputTokens: 10, remainingCostMicrousd: 10000 };
  for (const registry of [f.registry.map(m => ({ ...m, hosting: 'external' })), f.registry.map(m => ({ ...m, qualification: { ...m.qualification, expiresAt: 0 } }))]) assert.throws(() => routeModel({ ...args, registry }), /no_qualified_model/);
  assert.throws(() => routeModel({ ...args, registry: f.registry, task: { ...args.task, requiredCapabilities: ['audio'] } }), /no_qualified_model/);
  assert.throws(() => routeModel({ ...args, registry: f.registry, accountId: 'another-account' }), /no_qualified_model/);
});
test('reservation rejection prevents all inference', async () => {
  const f = fixture(); f.budget.reserve = async () => { throw Object.assign(new Error(), { name: 'SecurityError', code: 'account_budget_exhausted' }); };
  assert.equal((await orchestrate(f)).error.code, 'account_budget_exhausted'); assert.equal(f.dispatches.length, 0);
});
test('request ceiling cannot be exceeded by conservative reservation', async () => {
  const f = fixture(); f.envelope.limits.maxCostMicrousd = 100;
  assert.equal((await orchestrate(f)).error.code, 'request_budget_exhausted'); assert.equal(f.dispatches.length, 0);
});
test('unknown provider usage retains full reservation and cannot report success', async () => {
  const f = fixture(); f.env.AI.run = async () => ({ response: 'possibly correct' });
  const result = await orchestrate(f);
  assert.equal(result.error.code, 'provider_usage_unavailable'); assert.equal(result.text, '');
  assert.equal(result.usage.costEvidence, 'reserved_upper_bound'); assert(result.usage.costMicrousd > 1000); assert.equal(f.settlements[0].usageKnown, false);
});
test('cancelled in-flight binding returns promptly, debits conservatively and never retries', async () => {
  const abort = new AbortController(); const f = fixture({ signal: abort.signal });
  let calls = 0; f.env.AI.run = async () => { calls++; abort.abort(); return new Promise(() => {}); };
  const result = await orchestrate(f);
  assert.equal(result.error.code, 'cancelled'); assert.equal(calls, 1); assert.equal(f.settlements[0].usageKnown, false);
});
test('cancellation during reservation settles zero without dispatching model', async () => {
  const abort = new AbortController(); const f = fixture({ signal: abort.signal }); const reserve = f.budget.reserve;
  f.budget.reserve = async (...args) => { const receipt = await reserve(...args); abort.abort(); return receipt; };
  const result = await orchestrate(f);
  assert.equal(result.error.code, 'cancelled'); assert.equal(f.dispatches.length, 0); assert.equal(result.usage.costMicrousd, 0);
});
test('deadline bounds hanging provider and unknown charge survives', async () => {
  const f = fixture(); f.envelope.limits.deadlineUnixMs = Date.now() + 15; f.env.AI.run = async () => new Promise(() => {});
  const result = await orchestrate(f); assert.equal(result.error.code, 'deadline_exceeded'); assert.equal(f.settlements[0].usageKnown, false);
});
test('one cloud loop passes tool results with stable identity to next model turn', async () => {
  const f = fixture(); f.envelope.task.tools = [{ type: 'function', name: 'read_repository', parameters: { type: 'object' } }];
  let turn = 0; let executed = 0;
  f.env.AI.run = async (id, input) => {
    if (turn++ === 0) return response('', [call()]);
    assert.equal(input.messages.at(-1).tool_call_id, 'call-1'); assert(input.messages.at(-1).content.includes('sourceRevision'));
    return response('Source inspected.');
  };
  f.toolBroker = { async execute(value) { executed++; assert.equal(value.context, f.context); assert.match(value.idempotencyKey, /^[0-9a-f]{64}$/); return { output: { sourceRevision: 'abc', content: 'approved source' }, usage: { costMicrousd: 19, costEvidence: 'provider_usage' } }; } };
  const result = await orchestrate(f); assert.equal(result.status, 'completed'); assert.equal(executed, 1); assert.equal(result.toolResults.length, 1);
  assert.equal(result.usage.costMicrousd, 25);
});
test('whole tool batch is validated before any tool executes', async () => {
  const f = fixture(); f.envelope.task.tools = [{ name: 'read_repository' }];
  f.env.AI.run = async () => response('', [call(), call('call-2', 'delete_production')]);
  let executions = 0; f.toolBroker = { async execute() { executions++; } };
  assert.equal((await orchestrate(f)).error.code, 'unpermitted_or_duplicate_tool'); assert.equal(executions, 0);
});
test('iteration limit prevents tool side effects with no synthesis budget remaining', async () => {
  const f = fixture(); f.envelope.limits.maxIterations = 1; f.envelope.task.tools = [{ name: 'read_repository' }];
  f.env.AI.run = async () => response('', [call()]); let executions = 0; f.toolBroker = { async execute() { executions++; } };
  assert.equal((await orchestrate(f)).error.code, 'iteration_limit'); assert.equal(executions, 0);
});
test('duplicate tool call IDs across rounds cannot execute twice', async () => {
  const f = fixture(); f.envelope.task.tools = [{ name: 'read_repository' }]; f.env.AI.run = async () => response('', [call()]);
  let executions = 0; f.toolBroker = { async execute() { executions++; return { output: { ok: true }, usage: { costMicrousd: 0, costEvidence: 'provider_usage' } }; } };
  assert.equal((await orchestrate(f)).error.code, 'unpermitted_or_duplicate_tool'); assert.equal(executions, 1);
});
test('adapter rejects malformed, truncated and mismatched output and excludes reasoning', () => {
  const model = MODEL_REGISTRY[0];
  assert.throws(() => parseProviderResponse({ ...response('bad'), model: 'external-model' }, model), /provider_model_mismatch/);
  assert.throws(() => parseProviderResponse({ choices: [{ finish_reason: 'length' }] }, model), /provider_output_truncated/);
  assert.throws(() => parseProviderResponse(response('', [{ id: 'x', function: { name: 'read', arguments: 'invalid' } }]), model), /provider_invalid_tool_arguments/);
  const result = parseProviderResponse({ output: [{ type: 'reasoning', content: [{ text: 'private reasoning' }] }, { type: 'message', content: [{ type: 'output_text', text: 'answer' }] }], usage: { input_tokens: 1, output_tokens: 2 } }, model);
  assert.equal(result.text, 'answer');
});
test('GLM provider request bounds all completion tokens and disables storage', () => {
  const input = buildProviderInput(MODEL_REGISTRY[2], [], { tools: [] }, 512);
  assert.equal(input.max_completion_tokens, 512); assert.equal(input.store, false); assert.equal(input.reasoning_effort, 'low');
});
test('circuit excludes repeatedly failing engines until cooldown', () => {
  let now = 100; const circuit = new CircuitBreaker({ now: () => now, cooldownMs: 50 });
  circuit.failure('model'); circuit.failure('model'); circuit.failure('model'); assert.deepEqual(circuit.unavailable(), ['model']);
  now = 151; assert.deepEqual(circuit.unavailable(), []);
});
test('usage calculator rejects NaN and negative provider values', () => {
  assert.throws(() => estimateCostMicrousd(MODEL_REGISTRY[0], NaN, 1), /invalid_usage/);
  assert.throws(() => estimateCostMicrousd(MODEL_REGISTRY[0], 1, -1), /invalid_usage/);
});
test('event streaming emits one final result and no unverified token deltas', async () => {
  const f = fixture(); const stream = createEventStream(options => orchestrate({ ...f, ...options }));
  const output = await new Response(stream.readable).text(); await stream.completion;
  const events = output.trim().split('\n\n').map(line => JSON.parse(line.slice(6)));
  assert.deepEqual(events.map(event => event.type), ['progress', 'final']); assert.equal(events[1].response.text, '42');
});
test('reader cancellation immediately aborts pending provider and preserves unknown debit', async () => {
  const f = fixture(); let started;
  const dispatched = new Promise(resolve => { started = resolve; });
  f.env.AI.run = async () => { started(); return new Promise(() => {}); };
  const stream = createEventStream(options => orchestrate({ ...f, ...options }));
  const reader = stream.readable.getReader(); await reader.read(); await dispatched;
  await reader.cancel(); await stream.completion;
  assert.equal(f.settlements.length, 1); assert.equal(f.settlements[0].usageKnown, false);
});
test('cancelled tool debit is included from settled broker failure receipt', async () => {
  const f = fixture(); f.envelope.task.tools = [{ name: 'read_repository' }]; f.env.AI.run = async () => response('', [call()]);
  f.toolBroker = { async execute() { throw Object.assign(new Error(), { name: 'SecurityError', code: 'tool_cancelled', usage: { costMicrousd: 200, costEvidence: 'reserved_upper_bound' } }); } };
  const result = await orchestrate(f); assert.equal(result.error.code, 'tool_cancelled'); assert.equal(result.usage.costMicrousd, 203); assert.equal(result.usage.costEvidence, 'reserved_upper_bound');
});
test('over-reservation model settlement failure preserves observed charged usage', async () => {
  const f = fixture();
  f.env.AI.run = async () => ({ ...response('42'), usage: { prompt_tokens: 40000, completion_tokens: 5 } });
  f.budget.settle = async (context, value) => { throw Object.assign(new Error(), { name: 'SecurityError', code: 'provider_cost_exceeded_reservation', usage: { costMicrousd: value.actualCostMicrousd, costEvidence: 'provider_usage' } }); };
  const result = await orchestrate(f);
  assert.equal(result.error.code, 'provider_cost_exceeded_reservation'); assert.equal(result.text, '');
  assert.equal(result.usage.costMicrousd, 2038); assert.equal(result.usage.costEvidence, 'provider_usage'); assert.equal(result.usage.inputTokens, 40000);
});
test('lost model settlement acknowledgment retains full reserved debit', async () => {
  const f = fixture(); f.budget.settle = async () => { throw new Error('transport unavailable'); };
  const result = await orchestrate(f);
  assert.equal(result.status, 'failed'); assert.equal(result.usage.costEvidence, 'reserved_upper_bound');
  assert.equal(result.usage.costMicrousd, [...f.reservations.values()][0].maxCostMicrousd);
  assert.equal(result.usage.inputTokens, 20); assert.equal(result.usage.outputTokens, 5);
});
test('lost settlement reports known actual spend when larger than reservation', async () => {
  const f = fixture(); f.env.AI.run = async () => ({ ...response('42'), usage: { prompt_tokens: 40000, completion_tokens: 5 } });
  f.budget.settle = async () => { throw new Error('transport unavailable'); };
  const result = await orchestrate(f);
  assert.equal(result.status, 'failed'); assert.equal(result.usage.costEvidence, 'reserved_upper_bound'); assert.equal(result.usage.costMicrousd, 2038);
});
