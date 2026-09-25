import assert from 'node:assert/strict';
import test from 'node:test';
import { buildBridgeRequest, handleWebsiteRouting } from '../../src/website-routing/bridge.mjs';

const secret = '0123456789abcdef0123456789abcdef';
const env = {
  LEGEND_WEBSITE_BRIDGE_SECRET: secret,
  LEGEND_WEBSITE_ORIGIN: 'https://masterapp-protect.azurewebsites.net'
};

test('external SaaS hostname is bridged to the single Protect origin with authenticated original host', async () => {
  const request = new Request('https://camoexterior.com/services?utm_source=test', {
    headers: {
      'User-Agent': 'bridge-test',
      'X-Legend-Original-Host': 'spoof.example',
      'X-Legend-Website-Bridge': 'spoof'
    }
  });
  const result = buildBridgeRequest(request, env);
  assert.equal(result.error, undefined);
  assert.equal(result.request.url, 'https://masterapp-protect.azurewebsites.net/services?utm_source=test');
  assert.equal(result.request.headers.get('X-Legend-Original-Host'), 'camoexterior.com');
  assert.equal(result.request.headers.get('X-Legend-Website-Bridge'), secret);
  assert.equal(result.request.headers.get('User-Agent'), 'bridge-test');
});

test('POST body and content type survive the bridge', async () => {
  const request = new Request('https://client-business.example/api/website-inquiries/public', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ submissionId: 'abc' })
  });
  const result = buildBridgeRequest(request, env);
  assert.equal(result.request.method, 'POST');
  assert.equal(result.request.headers.get('Content-Type'), 'application/json');
  assert.equal(await result.request.text(), JSON.stringify({ submissionId: 'abc' }));
});

test('LEGEND-owned hosts bypass the bridge unchanged', async () => {
  const request = new Request('https://protect.mylegnd.com/');
  let seen;
  const response = await handleWebsiteRouting(request, env, async forwarded => {
    seen = forwarded;
    return new Response('ok');
  });
  assert.equal(await response.text(), 'ok');
  assert.equal(seen.url, request.url);
  assert.equal(seen.headers.get('X-Legend-Original-Host'), null);
});

test('external host fails closed when the shared bridge secret is unavailable', async () => {
  const request = new Request('https://business.example/');
  let called = false;
  const response = await handleWebsiteRouting(request, { LEGEND_WEBSITE_ORIGIN: env.LEGEND_WEBSITE_ORIGIN }, async () => {
    called = true;
    return new Response('unexpected');
  });
  assert.equal(response.status, 503);
  assert.equal(called, false);
});

test('invalid upstream configuration fails closed instead of becoming an open proxy', () => {
  const result = buildBridgeRequest(new Request('https://business.example/'), {
    LEGEND_WEBSITE_BRIDGE_SECRET: secret,
    LEGEND_WEBSITE_ORIGIN: 'https://example.com'
  });
  assert.equal(result.error.status, 503);
});

test('external HTTP requests are upgraded before proxying', async () => {
  const request = new Request('http://business.example/path?q=1');
  const response = await handleWebsiteRouting(request, env, async () => new Response('unexpected'));
  assert.equal(response.status, 308);
  assert.equal(response.headers.get('location'), 'https://business.example/path?q=1');
});
