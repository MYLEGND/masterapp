import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { envelope, harness, signedRequest, NOW } from './fixtures.mjs';

// Run after lead integration, or point to that exact checkout for independent
// review. The actual Worker entrypoint is loaded; no production key is used.
const entry = process.env.LEGEND_INTEGRATION_ROOT
  ? pathToFileURL(`${process.env.LEGEND_INTEGRATION_ROOT}/Legend-Cloudflare/src/index.mjs`)
  : new URL('../../src/index.mjs', import.meta.url);
const { default: worker } = await import(entry);

function setup() {
  const h = harness();
  h.advance(Date.now() - NOW);
  let modelCalls = 0;
  h.env.AI = { run: async () => { modelCalls++; throw new Error('unqualified inference must never dispatch'); } };
  const now = h.now();
  const body = envelope({ issuedAt: now, expiresAt: now + 120000,
    limits: { ...envelope().limits, deadlineUnixMs: now + 120000 } });
  const pending = [];
  return { h, body, calls: () => modelCalls, execution: { waitUntil(promise) { pending.push(promise); } }, pending };
}

test('actual Worker rejects forged tenant context without inference and returns no personalized cache', async () => {
  const { h, body, calls, execution } = setup();
  const request = signedRequest(body);
  const forged = new Request(request.url, { method: 'POST', headers: request.headers,
    body: (await request.text()).replace('tenant-1', 'tenant-private-customer') });
  const response = await worker.fetch(forged, h.env, execution);
  assert.equal(response.status, 401);
  assert.equal((await response.json()).error, 'signature_invalid');
  assert.equal(response.headers.get('Cache-Control'), 'no-store, private');
  assert.equal(calls(), 0);
});

test('actual Worker fails closed for unqualified cloud model, closes request and rejects replay', async () => {
  const { h, body, calls, execution } = setup();
  const response = await worker.fetch(signedRequest(body), h.env, execution);
  assert.equal(response.status, 503);
  const result = await response.json();
  assert.equal(result.error.code, 'no_qualified_model');
  assert.equal(result.status, 'failed');
  assert.equal(result.text, '');
  assert.equal(calls(), 0);
  const requestRecords = [...h.storage.data].filter(([key]) => key.startsWith('request:'));
  assert.ok(requestRecords.every(([, value]) => value.closed));
  h.restart();
  const replay = await worker.fetch(signedRequest(body), h.env, execution);
  assert.equal(replay.status, 409);
  assert.equal((await replay.json()).error, 'request_replayed');
});

test('actual Worker stream returns a scoped failure event with private/no-store headers', async () => {
  const { h, body, calls, execution, pending } = setup();
  body.stream = true;
  const response = await worker.fetch(signedRequest(body), h.env, execution);
  assert.equal(response.headers.get('Content-Type'), 'text/event-stream');
  assert.equal(response.headers.get('Cache-Control'), 'no-store, private');
  const text = await response.text();
  await Promise.all(pending);
  const event = JSON.parse(text.trim().slice('data: '.length));
  assert.equal(event.type, 'error');
  assert.equal(event.response.requestId, body.requestId);
  assert.equal(event.response.error.code, 'no_qualified_model');
  assert.equal(calls(), 0);
});
