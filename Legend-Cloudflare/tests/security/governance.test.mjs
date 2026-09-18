import test from 'node:test';
import assert from 'node:assert/strict';
import { createGovernanceClient } from '../../src/security/governance.mjs';
import { envelope, harness, reserve, NOW } from './fixtures.mjs';

test('request and nonce replay rejection survive Durable Object instance replacement', async () => {
  const h = harness();
  const nonce = 'a'.repeat(32);
  await h.session(envelope(), nonce);
  h.restart();
  await assert.rejects(h.session(envelope(), 'b'.repeat(32)), { code: 'request_replayed' });
  await assert.rejects(h.session(envelope({ requestId: 'request-2' }), nonce), { code: 'request_replayed' });
});

test('parallel requests atomically share the one account budget with no partial writes', async () => {
  const h = harness({ accountMicrousd: 100 });
  const sessions = await Promise.all(Array.from({ length: 20 }, (_, i) => h.session(envelope({ requestId: `request-${i}` }))));
  const results = await Promise.allSettled(sessions.map(session => reserve(session, 'model-1', 20)));
  assert.equal(results.filter(result => result.status === 'fulfilled').length, 5);
  assert.ok(results.filter(result => result.status === 'rejected').every(result => result.reason.code === 'account_budget_exhausted'));
  const charges = [...h.storage.data.entries()].filter(([key]) => key.startsWith('quota:')).map(([, value]) => value.chargedMicrousd);
  assert.deepEqual(charges, [100, 100, 100]);
  const leases = [...h.storage.data.entries()].filter(([key]) => key.startsWith('leases:')).map(([, value]) => value.active.length);
  assert.equal(leases.filter(count => count === 5).length, 3);
});

test('request, user, tenant and account ceilings are independently enforced', async () => {
  for (const scope of ['request', 'user', 'tenant', 'account']) {
    const h = harness({ [`${scope}Microusd`]: 99 });
    const session = await h.session();
    await assert.rejects(reserve(session), { code: `${scope}_budget_exhausted` });
    assert.equal([...h.storage.data.keys()].filter(key => key.startsWith('quota:') || key.startsWith('leases:')).length, 0);
  }
});

test('each concurrency ceiling is independent and failures roll back other scopes', async () => {
  for (const scope of ['request', 'user', 'tenant', 'account']) {
    const h = harness({ [`${scope}Concurrency`]: 1 });
    const first = await h.session();
    await reserve(first, 'first');
    const second = scope === 'request' ? first : await h.session(envelope({ requestId: 'request-2' }));
    await assert.rejects(reserve(second, 'second'), { code: `${scope}_concurrency_exhausted` });
  }
});

test('known usage refunds only its unused reservation; repeat settlement is idempotent', async () => {
  const h = harness({ requestMicrousd: 100 });
  const session = await h.session();
  await reserve(session);
  const input = { reservationId: 'reservation-1', actualCostMicrousd: 20, usageKnown: true };
  assert.equal((await session.budget.settle(session.context, input)).chargedMicrousd, 20);
  h.restart();
  assert.equal((await session.budget.settle(session.context, input)).chargedMicrousd, 20);
  await reserve(session, 'reservation-2', 80);
  await assert.rejects(reserve(session, 'reservation-3', 1), { code: 'request_budget_exhausted' });
  await assert.rejects(session.budget.settle(session.context, { ...input, actualCostMicrousd: 0 }), { code: 'settlement_conflict' });
});

test('unknown usage keeps full cost and concurrency even when caller closes the request', async () => {
  const h = harness({ accountConcurrency: 1 });
  const first = await h.session();
  await reserve(first);
  assert.equal((await first.budget.settle(first.context, { reservationId: 'reservation-1', usageKnown: false })).chargedMicrousd, 100);
  await first.budget.close();
  h.restart();
  const second = await h.session(envelope({ requestId: 'request-2' }));
  await assert.rejects(reserve(second), { code: 'account_concurrency_exhausted' });
  await assert.rejects(first.budget.settle(first.context, { reservationId: 'reservation-1', usageKnown: true, actualCostMicrousd: 0 }),
    { code: 'settlement_conflict' });
  h.advance(120001);
  const third = await h.session(envelope({ requestId: 'request-3', issuedAt: h.now(), expiresAt: h.now() + 120000,
    limits: { ...envelope().limits, deadlineUnixMs: h.now() + 120000 } }));
  await reserve(third);
});

test('verified completion releases a lease while keeping unknown usage fully charged and immutable', async () => {
  const h = harness({ accountConcurrency: 1, accountMicrousd: 120 });
  const first = await h.session(); await reserve(first);
  const settlement = { reservationId: 'reservation-1', usageKnown: false, actualCostMicrousd: 0, executionCompleted: true };
  assert.equal((await first.budget.settle(first.context, settlement)).chargedMicrousd, 100);
  h.restart();
  assert.equal((await first.budget.settle(first.context, settlement)).chargedMicrousd, 100);
  await assert.rejects(first.budget.settle(first.context, { ...settlement, usageKnown: true }), { code: 'settlement_conflict' });
  const second = await h.session(envelope({ requestId: 'request-2' }));
  await reserve(second, 'second-model', 20);
  await second.budget.settle(second.context, { reservationId: 'second-model', usageKnown: true, actualCostMicrousd: 20 });
  await assert.rejects(reserve(second, 'excess', 1), { code: 'account_budget_exhausted' });
});

test('uncertain execution cannot later be upgraded to completion to release its lease', async () => {
  const h = harness({ accountConcurrency: 1 });
  const first = await h.session(); await reserve(first);
  await assert.rejects(first.budget.settle(first.context,
    { reservationId: 'reservation-1', usageKnown: false, executionCompleted: 'true' }), { code: 'settlement_invalid' });
  await first.budget.settle(first.context, { reservationId: 'reservation-1', usageKnown: false });
  h.restart();
  await assert.rejects(first.budget.settle(first.context,
    { reservationId: 'reservation-1', usageKnown: false, executionCompleted: true }), { code: 'settlement_conflict' });
  const second = await h.session(envelope({ requestId: 'request-2' }));
  await assert.rejects(reserve(second), { code: 'account_concurrency_exhausted' });
});

test('scope substitution and duplicate reservation cannot execute again', async () => {
  const h = harness();
  const session = await h.session();
  await reserve(session);
  await assert.rejects(reserve(session), { code: 'reservation_replayed' });
  await assert.rejects(session.budget.reserve({ ...session.context, tenantId: 'tenant-2' }, {}), { code: 'context_substitution' });
  const wrong = { ...session.context, userId: 'user-2' };
  const wrongPort = createGovernanceClient(h.env, wrong);
  await assert.rejects(wrongPort.reserve(wrong, { requestId: wrong.requestId, reservationId: 'new', maxCostMicrousd: 1,
    deadlineUnixMs: wrong.deadlineUnixMs }), { code: 'request_scope_mismatch' });
});

test('reported overspend is recorded, never silently capped, and execution fails closed', async () => {
  const h = harness({ accountMicrousd: 120 });
  const first = await h.session(); await reserve(first);
  await assert.rejects(first.budget.settle(first.context,
    { reservationId: 'reservation-1', usageKnown: true, actualCostMicrousd: 130 }), { code: 'provider_cost_exceeded_reservation' });
  const second = await h.session(envelope({ requestId: 'request-2' }));
  await assert.rejects(reserve(second, 'another', 1), { code: 'account_budget_exhausted' });
});

test('no implicit budget or missing durable binding can permit model dispatch', async () => {
  const h = harness();
  h.env.LEGEND_BUDGET_POLICY_JSON = '{}';
  await assert.rejects(h.session(), { code: 'budget_configuration_invalid' });
  assert.throws(() => createGovernanceClient({}, {}), { code: 'governance_binding_missing' });
});

test('lifetime qualification budget survives request retention deletion and never refills', async () => {
  const h = harness({ period: 'lifetime', retentionMs: 120000, accountMicrousd: 100 });
  const first = await h.session(); await reserve(first);
  await first.budget.settle(first.context, { reservationId: 'reservation-1', usageKnown: true, actualCostMicrousd: 100 });
  h.advance(86400000);
  await h.alarm(); h.restart();
  assert.equal([...h.storage.data.keys()].filter(key => key.startsWith('request:')).length, 0);
  const second = await h.session(envelope({ requestId: 'new-day', issuedAt: h.now(), expiresAt: h.now() + 120000,
    limits: { ...envelope().limits, deadlineUnixMs: h.now() + 120000 } }));
  await assert.rejects(reserve(second, 'first', 1), { code: 'account_budget_exhausted' });
});

test('calendar-month quota resets by UTC month, while concurrency does not reset at a period boundary', async () => {
  const h = harness({ period: 'calendar-month', accountMicrousd: 100, accountConcurrency: 1 });
  h.advance(Date.UTC(2026, 8, 30, 23, 59, 30) - NOW);
  const first = await h.session(envelope({ issuedAt: h.now(), expiresAt: h.now() + 120000,
    limits: { ...envelope().limits, deadlineUnixMs: h.now() + 120000 } }));
  await reserve(first);
  await first.budget.settle(first.context, { reservationId: 'reservation-1', usageKnown: false });
  h.advance(31000);
  const second = await h.session(envelope({ requestId: 'next-month', issuedAt: h.now(), expiresAt: h.now() + 120000,
    limits: { ...envelope().limits, deadlineUnixMs: h.now() + 120000 } }));
  await assert.rejects(reserve(second), { code: 'account_concurrency_exhausted' });
  h.advance(90000);
  await reserve(second);
});
