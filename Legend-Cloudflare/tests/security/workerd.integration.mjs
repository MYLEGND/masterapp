import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { envelope, environment, signedRequest } from './fixtures.mjs';

// Optional local dependency is supplied by the lead-owned manifest or an
// explicit installed module path. Missing workerd fails this dedicated test;
// it does not silently turn a simulator test into claimed platform evidence.
const { Miniflare, convertV4MiniflareOptions } = await import(process.env.LEGEND_MINIFLARE_MODULE ?? 'miniflare');

test('real local workerd SQLite Durable Object enforces concurrent account cap and restart replay', async () => {
  const storagePath = await mkdtemp(join(tmpdir(), 'legend-security-workerd-'));
  const env = environment({ accountMicrousd: 100 });
  const securityPath = fileURLToPath(new URL('../../src/security/', import.meta.url));
  const modules = [{ type: 'ESModule', path: fileURLToPath(new URL('./workerd-fixture.mjs', import.meta.url)) },
    { type: 'ESModule', path: fileURLToPath(new URL('../../src/runtime/registry.mjs', import.meta.url)) },
    ...(await readdir(securityPath)).filter(file => file.endsWith('.mjs')).map(file => ({ type: 'ESModule', path: join(securityPath, file) }))];
  const options = { name: 'legend-security-local-test', modules,
    compatibilityDate: '2026-09-18', bindings: env,
    durableObjects: { LEGEND_GOVERNANCE: { className: 'LegendGovernance', useSQLite: true } },
    durableObjectsPersist: storagePath,
    // This test has no AI binding and cannot send any external HTTP request.
    outboundService: () => new Response('External network forbidden by test', { status: 403 }),
  };
  const createRuntime = () => new Miniflare(convertV4MiniflareOptions
    ? { ...convertV4MiniflareOptions(options), resourcePersistencePath: storagePath, telemetry: { enabled: false } } : options);
  let mf;
  const now = Date.now();
  const bodies = Array.from({ length: 20 }, (_, index) => envelope({ requestId: `request-${index}`,
    issuedAt: now, expiresAt: now + 120000, limits: { ...envelope().limits, deadlineUnixMs: now + 120000 } }));
  async function dispatch(body, index) {
    const request = signedRequest(body, { nonce: String(index).padStart(32, 'a') });
    const response = await mf.dispatchFetch(request.url, { method: request.method, headers: Object.fromEntries(request.headers), body: await request.text() });
    return { status: response.status, body: await response.json() };
  }
  try {
    mf = createRuntime();
    await mf.ready;
    const responses = await Promise.all(bodies.map(dispatch));
    assert.equal(responses.filter(response => response.status === 200).length, 5);
    assert.equal(responses.filter(response => response.status === 429).length, 15);
    for (const response of responses.filter(response => response.status === 429)) {
      assert.equal(response.body.error, 'account_budget_exhausted');
    }
    await mf.dispose();
    mf = createRuntime();
    await mf.ready;
    const replay = await dispatch(bodies[0], 0);
    assert.equal(replay.status, 409);
    assert.equal(replay.body.error, 'request_replayed');
  } finally {
    await mf?.dispose();
    await rm(storagePath, { recursive: true, force: true });
  }
});
