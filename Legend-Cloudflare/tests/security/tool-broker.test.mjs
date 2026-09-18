import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac, createHash } from 'node:crypto';
import { createToolBroker, toolActionDigest } from '../../src/security/tool-broker.mjs';
import { executeGeneratedCode, CLOUD_EXECUTION_STATUS } from '../../src/security/sandbox.mjs';
import { canonicalJson, sha256 } from '../../src/security/crypto.mjs';
import { envelope, harness, TEST_KEY } from './fixtures.mjs';

const tool = { type: 'function', name: 'legend_inspect_repository', parameters: { type: 'object' } };
const call = { id: 'call-1', name: tool.name, arguments: { path: 'src/main.cs', revision: 'a'.repeat(40) } };

async function setup(fetcher, policy = {}) {
  const h = harness(policy);
  Object.assign(h.env, { LEGEND_TOOL_CALLBACK_ENABLED: 'true', LEGEND_AZURE_TOOL_CALLBACK_URL: 'https://azure.example/tools/callback',
    LEGEND_TOOL_CALLBACK_KEY_ID: 'worker-v1', LEGEND_TOOL_CALLBACK_SECRET: TEST_KEY,
    LEGEND_TOOL_MAX_COST_MICROUSD: '100', LEGEND_DEPLOYMENT_ENVIRONMENT: 'qualification' });
  const body = envelope(); body.task.tools = [tool];
  const session = await h.session(body);
  const broker = createToolBroker({ env: h.env, ...session, now: h.now, fetcher });
  const input = { context: session.context, call, idempotencyKey: await sha256(`${session.context.requestId}:tool:${call.id}`),
    signal: new AbortController().signal };
  return { h, session, broker, input };
}

function responseFor(body, override = {}) {
  return Response.json({ version: 'legend-tool-receipt.v1', requestId: body.requestId, contextDigest: body.contextDigest,
    toolCallId: body.call.id, actionDigest: body.actionDigest, idempotencyKey: body.idempotencyKey,
    authorizationVersion: body.scope.authorizationVersion, reauthorized: true, output: { content: 'Scoped source.' },
    usage: { known: true, costMicrousd: 25 }, ...override });
}

test('tool callback uses only configured Azure URL, signs exact scope/action and returns accounted receipt', async () => {
  let seen;
  const { broker, input } = await setup(async (url, options) => {
    seen = JSON.parse(options.body);
    assert.equal(url, 'https://azure.example/tools/callback');
    assert.equal(options.redirect, 'error');
    assert.equal(options.cache, 'no-store');
    const headers = options.headers;
    const digest = createHash('sha256').update(options.body).digest('hex');
    const message = ['legend-service.v1', 'POST', '/tools/callback', headers['X-Legend-Key-Id'],
      headers['X-Legend-Timestamp'], headers['X-Legend-Nonce'], digest].join('\n');
    assert.equal(headers['X-Legend-Signature'], createHmac('sha256', Buffer.from(TEST_KEY, 'base64')).update(message).digest('hex'));
    return responseFor(seen);
  });
  const receipt = await broker.execute(input);
  assert.equal(seen.scope.tenantId, 'tenant-1');
  assert.equal(seen.actionDigest, await toolActionDigest(input.context, call, 'qualification'));
  assert.deepEqual(receipt, { output: { content: 'Scoped source.' }, usage: { costMicrousd: 25, costEvidence: 'provider_usage' } });
});

test('32 KiB UTF-8 output fits its receipt metadata but one additional output byte cannot reach the model', async () => {
  const overhead = Buffer.byteLength(JSON.stringify({ content: '' }));
  for (const extra of [0, 1]) {
    const output = { content: 'é'.repeat((32768 - overhead) / 2) + 'x'.repeat(extra) };
    assert.equal(Buffer.byteLength(JSON.stringify(output)), 32768 + extra);
    const { broker, input, h } = await setup(async (_, options) => {
      const response = responseFor(JSON.parse(options.body), {
        output, usage: { known: false, costMicrousd: 100, costEvidence: 'reserved_upper_bound' },
      });
      const bytes = (await response.clone().arrayBuffer()).byteLength;
      assert.ok(bytes > 32768 && bytes <= 65536);
      return response;
    });
    if (extra === 0) {
      assert.deepEqual(await broker.execute(input), { output, usage: { costMicrousd: 100, costEvidence: 'reserved_upper_bound' } });
    } else {
      await assert.rejects(broker.execute(input), error => error.code === 'tool_output_too_large' &&
        error.usage.costMicrousd === 100 && error.usage.costEvidence === 'reserved_upper_bound');
      assert.equal([...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1].executionCompleted, false);
    }
  }
});

test('wrapped receipt limit is exactly 64 KiB independent of its small output', async () => {
  for (const size of [65536, 65537]) {
    const { broker, input, h } = await setup(async (_, options) => {
      const receipt = await responseFor(JSON.parse(options.body), { padding: '' }).json();
      receipt.padding = 'x'.repeat(size - Buffer.byteLength(JSON.stringify(receipt)));
      const serialized = JSON.stringify(receipt);
      assert.equal(Buffer.byteLength(serialized), size);
      return new Response(serialized, { headers: { 'Content-Type': 'application/json' } });
    });
    if (size === 65536) {
      assert.deepEqual(await broker.execute(input), {
        output: { content: 'Scoped source.' }, usage: { costMicrousd: 25, costEvidence: 'provider_usage' },
      });
    } else {
      await assert.rejects(broker.execute(input), error => error.code === 'body_too_large' &&
        error.usage.costMicrousd === 100 && error.usage.costEvidence === 'reserved_upper_bound');
      assert.equal([...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1].executionCompleted, false);
    }
  }
});

test('completed tool with explicit operator bound retains its full debit and permits the next model at concurrency one', async () => {
  const { broker, input, session, h } = await setup(async (_, options) => {
    const body = JSON.parse(options.body);
    return responseFor(body, { usage: { known: false, costMicrousd: body.maxCostMicrousd, costEvidence: 'reserved_upper_bound' } });
  }, { requestConcurrency: 1, userConcurrency: 1, tenantConcurrency: 1, accountConcurrency: 1 });
  assert.deepEqual(await broker.execute(input), {
    output: { content: 'Scoped source.' }, usage: { costMicrousd: 100, costEvidence: 'reserved_upper_bound' },
  });
  h.restart();
  const reservation = [...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1];
  assert.equal(reservation.chargedMicrousd, 100);
  assert.equal(reservation.usageKnown, false);
  assert.equal(reservation.executionCompleted, true);
  assert.ok([...h.storage.data].filter(([key]) => key.startsWith('leases:')).every(([, value]) => value.active.length === 0));
  assert.ok([...h.storage.data].filter(([key]) => key.startsWith('quota:')).every(([, value]) => value.chargedMicrousd === 100));
  await session.budget.reserve(session.context, { requestId: session.context.requestId, reservationId: 'next-model',
    maxCostMicrousd: 900, deadlineUnixMs: session.context.deadlineUnixMs });
  await assert.rejects(session.budget.reserve(session.context, { requestId: session.context.requestId, reservationId: 'excess',
    maxCostMicrousd: 1, deadlineUnixMs: session.context.deadlineUnixMs }), { code: 'request_budget_exhausted' });
  await assert.rejects(broker.execute(input), { code: 'reservation_replayed' });
});

test('missing, contradictory or mismatched conservative cost evidence never releases output or refunds unknown work', async () => {
  for (const usage of [undefined, { known: false }, { known: false, costMicrousd: 100 },
    { known: false, costMicrousd: 0, costEvidence: 'reserved_upper_bound' },
    { known: false, costMicrousd: 99, costEvidence: 'reserved_upper_bound' },
    { known: false, costMicrousd: 101, costEvidence: 'reserved_upper_bound' },
    { known: false, costMicrousd: '100', costEvidence: 'reserved_upper_bound' },
    { known: false, costMicrousd: 100, costEvidence: 'provider_usage' },
    { known: true, costMicrousd: 100, costEvidence: 'reserved_upper_bound' }]) {
    const { broker, input, h } = await setup(async (_, options) => responseFor(JSON.parse(options.body),
      { usage, executionCompleted: true }));
    await assert.rejects(broker.execute(input), error => error.code === 'tool_usage_invalid' &&
      error.usage.costMicrousd === 100 && error.usage.costEvidence === 'reserved_upper_bound');
    const reservation = [...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1];
    assert.equal(reservation.chargedMicrousd, 100);
    assert.equal(reservation.usageKnown, false);
    assert.equal(reservation.executionCompleted, false);
    assert.ok([...h.storage.data].filter(([key]) => key.startsWith('leases:')).every(([, value]) => value.active.length === 1));
  }
});

test('completed upper-bound receipt still requires exact action and successful reauthorization', async () => {
  for (const override of [{ reauthorized: false }, { actionDigest: 'x'.repeat(64) },
    { authorizationVersion: 'revoked' }, { contextDigest: 'x'.repeat(64) }]) {
    const { broker, input, h } = await setup(async (_, options) => responseFor(JSON.parse(options.body), {
      usage: { known: false, costMicrousd: 100, costEvidence: 'reserved_upper_bound' }, ...override,
    }));
    await assert.rejects(broker.execute(input), error => error.code === 'tool_receipt_invalid' && error.usage.costMicrousd === 100);
    assert.equal([...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1].executionCompleted, false);
  }
});

test('lost settlement acknowledgment cannot release even a verified completed upper-bound output', async () => {
  const { session, input, h } = await setup(() => {});
  const broker = createToolBroker({ env: h.env, context: session.context, now: h.now,
    budget: { reserve: session.budget.reserve, settle: async (...args) => {
      await session.budget.settle(...args);
      throw new Error('lost acknowledgment');
    } },
    fetcher: async (_, options) => responseFor(JSON.parse(options.body), {
      usage: { known: false, costMicrousd: 100, costEvidence: 'reserved_upper_bound' },
    }),
  });
  await assert.rejects(broker.execute(input), error => error.code === 'governance_unavailable' &&
    error.usage.costMicrousd === 100 && error.usage.costEvidence === 'reserved_upper_bound');
  assert.equal([...h.storage.data].find(([key]) => key.startsWith('reservation:'))[1].chargedMicrousd, 100);
});

test('action digest binds user, resource, environment, revision and exact argument semantics', async () => {
  const { input } = await setup(() => { throw new Error('must not run'); });
  const digest = await toolActionDigest(input.context, call, 'qualification');
  for (const [ctx, nextCall, environment] of [
    [{ ...input.context, userId: 'user-2' }, call, 'qualification'],
    [input.context, { ...call, arguments: { ...call.arguments, path: 'customer/private' } }, 'qualification'],
    [input.context, { ...call, arguments: { ...call.arguments, revision: 'b'.repeat(40) } }, 'qualification'],
    [input.context, call, 'production'],
    [input.context, { ...call, arguments: { ...call.arguments, diffDigest: 'new-diff' } }, 'qualification'],
  ]) assert.notEqual(await toolActionDigest(ctx, nextCall, environment), digest);
  assert.equal(await toolActionDigest(input.context,
    { ...call, arguments: { revision: call.arguments.revision, path: call.arguments.path } }, 'qualification'), digest);
});

test('caller mutation after invocation cannot change the signed action or transmitted arguments', async () => {
  let sent;
  const { broker, input } = await setup(async (_, options) => {
    sent = JSON.parse(options.body);
    return responseFor(sent);
  });
  input.call = structuredClone(call);
  const pending = broker.execute(input);
  input.call.arguments.path = 'unapproved/private/path';
  await pending;
  assert.equal(sent.call.arguments.path, call.arguments.path);
  assert.equal(sent.actionDigest, await toolActionDigest(input.context, call, 'qualification'));
});

test('model confirmation flags grant nothing: Azure denial is enforced and no retry occurs', async () => {
  let calls = 0;
  const { broker, input } = await setup(async () => { calls++; return Response.json({ reauthorized: false }, { status: 403 }); });
  input.call = { ...call, arguments: { ...call.arguments, confirmed: true, approved: true, founderCommandConfirmed: true } };
  await assert.rejects(broker.execute(input), error => error.code === 'tool_authorization_or_execution_failed' &&
    error.usage.costMicrousd === 100 && error.usage.costEvidence === 'reserved_upper_bound');
  await assert.rejects(broker.execute(input), { code: 'reservation_replayed' });
  assert.equal(calls, 1);
});

test('stale permissions, cross-scope receipts and substituted digests cannot release tool output', async () => {
  for (const override of [{ authorizationVersion: 'revoked-permission-version' }, { contextDigest: 'x'.repeat(64) },
    { actionDigest: 'x'.repeat(64) }, { reauthorized: false }, { idempotencyKey: 'another-request' }]) {
    const { broker, input } = await setup(async (_, options) => responseFor(JSON.parse(options.body), override));
    await assert.rejects(broker.execute(input), { code: 'tool_receipt_invalid' });
  }
});

test('unpublished tools, context overrides, malformed idempotency and canceled requests never dispatch', async () => {
  let calls = 0;
  const { broker, input } = await setup(async () => { calls++; throw new Error('unexpected callback'); });
  await assert.rejects(broker.execute({ ...input, call: { ...call, name: 'execute_shell' } }), { code: 'tool_not_authorized' });
  await assert.rejects(broker.execute({ ...input, context: { ...input.context, tenantId: 'tenant-2' } }), { code: 'context_substitution' });
  await assert.rejects(broker.execute({ ...input, idempotencyKey: 'model-chosen' }), { code: 'tool_idempotency_invalid' });
  await assert.rejects(broker.execute({ ...input, signal: AbortSignal.abort() }), { code: 'tool_cancelled' });
  assert.equal(calls, 0);
});

test('unverified callback, local endpoint and missing execution-cost ceiling fail closed', async () => {
  for (const override of [{ LEGEND_TOOL_CALLBACK_ENABLED: 'false' }, { LEGEND_AZURE_TOOL_CALLBACK_URL: 'http://localhost:8000/run' },
    { LEGEND_TOOL_MAX_COST_MICROUSD: undefined }, { LEGEND_TOOL_MAX_COST_MICROUSD: '1000000001' }]) {
    let calls = 0;
    const { h, broker, input } = await setup(async () => { calls++; });
    Object.assign(h.env, override);
    await assert.rejects(broker.execute(input));
    assert.equal(calls, 0);
  }
});

test('lost callback and cancellation retain upper-bound charge and lease before returning', async () => {
  let dispatched;
  const started = new Promise(resolve => { dispatched = resolve; });
  const { session, broker, input, h } = await setup(() => { dispatched(); return new Promise(() => {}); }, { accountConcurrency: 1 });
  const cancellation = new AbortController();
  const pending = broker.execute({ ...input, signal: cancellation.signal });
  await started;
  cancellation.abort();
  await assert.rejects(pending, error => error.code === 'tool_cancelled' && error.usage.costMicrousd === 100);
  h.restart();
  const second = await h.session(envelope({ requestId: 'request-2' }));
  await assert.rejects(second.budget.reserve(second.context, { requestId: 'request-2', reservationId: 'model',
    maxCostMicrousd: 1, deadlineUnixMs: second.context.deadlineUnixMs }), { code: 'account_concurrency_exhausted' });
  await session.budget.close();
});

test('cloud generated-code execution remains explicitly blocked without a verified sandbox', async () => {
  assert.equal(CLOUD_EXECUTION_STATUS.enabled, false);
  await assert.rejects(executeGeneratedCode({ code: 'arbitrary generated script' }), { code: 'cloud_sandbox_unverified' });
});

test('canonical approval serialization rejects extreme nesting and non-JSON values', () => {
  assert.throws(() => canonicalJson({ value: undefined }), { code: 'action_invalid' });
  let item = {};
  for (let index = 0; index < 40; index++) item = { item };
  assert.throws(() => canonicalJson(item), { code: 'action_too_complex' });
});
