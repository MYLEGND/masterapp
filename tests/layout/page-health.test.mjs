import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../SHARED/wwwroot/js/page-health.js', import.meta.url), 'utf8');
const endpoint = '/api/runtime-diagnostics';
const response = (status, extra = {}) => ({ ok: status >= 200 && status < 300, status, headers: { get: () => null }, ...extra });
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 8; i++) await Promise.resolve(); }
function fixture({ fetch = async () => response(200), ingest = async () => response(202), metadata = {}, online = true } = {}) {
  let now = 1000000, nextTimer = 0;
  const timers = new Map(), listeners = new Map(), calls = [], storage = [], navigation = [];
  class Clock extends Date { constructor(...values) { super(...(values.length ? values : [now])); } static now() { return now; } }
  const navigator = { onLine: online };
  const window = {
    fetch: async (input, init) => {
      calls.push({ input, init });
      return (String(input) === endpoint ? ingest : fetch)(input, init);
    },
    location: { pathname: '/client/private-customer', search: '?email=private@example.test', origin: 'https://portal.example.test', assign: value => navigation.push(value) },
    localStorage: { getItem() { throw new Error('Private diagnostics must never be read'); }, setItem() { throw new Error('Private diagnostics must never be stored'); }, removeItem: key => storage.push(key) },
    addEventListener: (name, handler) => listeners.set(name, handler),
    setTimeout: (callback, delay) => { const id = ++nextTimer; timers.set(id, { callback, when: now + delay }); return id; },
    clearTimeout: id => timers.delete(id)
  };
  const document = {
    currentScript: { dataset: { app: 'AgentPortal', route: '/Clients/Index', csrf: 'anti-forgery-fixture', founder: 'false', ...metadata } },
    scripts: [{ src: 'https://portal.example.test/js/clients-index.js?v=public-build' }, { src: 'https://portal.example.test/_content/Shared/js/page-health.js' }],
    title: 'Private customer title',
    createElement() { assert.fail('The collector must not render a diagnostics UI'); }
  };
  const context = vm.createContext({ window, document, navigator, URL, Date: Clock, Error, AbortController });
  const code = source.replace('window.LegendPageHealth = Object.freeze({ current });', 'window.LegendPageHealth = Object.freeze({ current }); window.testState = state;');
  vm.runInContext(code, context);
  return {
    window, listeners, navigator, calls, timers, storage, navigation, state: window.testState,
    api: window.LegendPageHealth.current,
    report: () => JSON.parse(window.LegendPageHealth.current.exportReport()),
    submissions: () => calls.filter(call => String(call.input) === endpoint),
    reload: () => vm.runInContext(code, context),
    async tick(duration) {
      const until = now + duration;
      for (let count = 0; count < 1000; count++) {
        const next = [...timers].filter(([, timer]) => timer.when <= until).sort((a, b) => a[1].when - b[1].when)[0];
        if (!next) { now = until; await flush(); return; }
        now = next[1].when; timers.delete(next[0]); next[1].callback(); await flush();
      }
      assert.fail('Unbounded timer loop');
    }
  };
}

test('existing API silently discards private caller content and obsolete storage; only structural script locations survive', async () => {
  const f = fixture();
  const error = new TypeError('secret-body private@example.test');
  error.stack = 'TypeError: secret-body\n at privateCustomer (https://portal.example.test/js/clients-index.js?token=secret-token:18:7)\n at privateCustomer (https://portal.example.test/private/customer-123.js:8:2)\n at remote (https://outside.test/js/clients-index.js:4:2)';
  f.api.log('private message', { email: 'private@example.test' });
  f.api.warn('secret-body', { error, customerId: 'customer-123', correlationId: 'customer-123', request: { body: 'secret-body' } });
  f.listeners.get('error')({ error, filename: 'https://portal.example.test/js/clients-index.js?token=secret-token', lineno: 18, colno: 7, message: 'secret-body' });
  await f.tick(1000);
  const saved = JSON.stringify(f.report()) + JSON.stringify(f.submissions().map(call => JSON.parse(call.init.body)));
  for (const privateValue of ['secret-body', 'private@example.test', 'privateCustomer', 'customer-123', 'secret-token', 'outside.test', 'Private customer title']) assert.ok(!saved.includes(privateValue), privateValue);
  assert.equal(f.state.events[0].payload.stackTrace, '/js/clients-index.js:18:7');
  assert.equal(f.state.events[0].payload.correlationId, '');
  assert.deepEqual(f.storage, ['legend_page_health_learning_v1']);
  assert.equal(typeof f.api.open, 'function'); assert.equal(typeof f.api.close, 'function'); assert.equal(typeof f.api.clearSession, 'function');
  assert.deepEqual(f.report().learnedPatterns, {});
});

test('untrusted route metadata and arbitrary error names never become private diagnostics', async () => {
  const f = fixture({ metadata: { app: 'private-account', route: '/Customers/123?email=private@example.test' } });
  f.api.error('private', { name: 'private@example.test', stack: 'private stack', correlationId: 'private-account' });
  await f.tick(1000);
  const event = JSON.parse(f.submissions()[0].init.body);
  assert.equal(event.route, '/'); assert.equal(event.appIdentifier, 'Web'); assert.equal(event.errorName, 'Error');
  assert.ok(!JSON.stringify(event).includes('private'));
});

test('deliberate cancellation is rethrown unchanged and never duplicated by global rejection handling', async () => {
  const controller = new AbortController(); controller.abort();
  const error = controller.signal.reason, f = fixture({ fetch: async () => { throw error; } });
  await assert.rejects(f.window.fetch('/data', { signal: controller.signal }), value => value === error);
  f.listeners.get('unhandledrejection')({ reason: error });
  assert.equal(f.state.events.length, 0); assert.equal(f.timers.size, 0);
});

test('explicit request timeout is observed even when fetch exposes AbortError', async () => {
  const timeout = new DOMException('private timeout detail', 'TimeoutError'), controller = new AbortController(); controller.abort(timeout);
  const error = new DOMException('private abort detail', 'AbortError');
  const f = fixture({ fetch: async () => { throw error; } });
  await assert.rejects(f.window.fetch('/data', { signal: controller.signal }), value => value === error);
  assert.equal(f.state.events[0].payload.errorName, 'TimeoutError');
  assert.ok(!f.api.exportReport().includes('private'));
});

test('unrelated transport error racing cancellation remains one network observation, not a script defect', async () => {
  const controller = new AbortController(); controller.abort(); const error = new TypeError('Failed to fetch customer-secret');
  const f = fixture({ fetch: async () => { throw error; } });
  await assert.rejects(f.window.fetch('/data', { signal: controller.signal }), value => value === error);
  f.listeners.get('unhandledrejection')({ reason: error });
  assert.equal(f.state.events.length, 1); assert.equal(f.state.events[0].sessionCount, 1);
  assert.equal(f.state.events[0].payload.errorName, 'NetworkError');
});

test('one wrapper preserves responses and never retries application requests or claims incident recovery', async () => {
  let count = 0; const failed = response(500), good = response(200);
  const f = fixture({ fetch: async () => ++count === 1 ? failed : good });
  const wrapped = f.window.fetch; f.reload(); assert.equal(f.window.fetch, wrapped);
  assert.equal(await f.window.fetch('/data?token=secret-token', { method: 'POST', body: 'private-body' }), failed);
  assert.equal(await f.window.fetch('/data?token=secret-token'), good);
  await f.tick(100000);
  assert.equal(count, 2); assert.equal(f.state.events.length, 1); assert.equal(f.submissions().length, 1);
  assert.ok(!f.api.exportReport().includes('secret-token')); assert.ok(!f.api.exportReport().includes('private-body'));
});

test('only durable 202 acknowledges an event; same-origin antiforgery request carries no application content', async () => {
  const f = fixture(); f.api.error('ignored', new Error('ignored')); await f.tick(1000);
  const request = f.submissions()[0];
  assert.equal(request.input, endpoint); assert.equal(request.init.method, 'POST'); assert.equal(request.init.credentials, 'same-origin');
  assert.equal(request.init.redirect, 'manual'); assert.equal(request.init.headers.RequestVerificationToken, 'anti-forgery-fixture');
  assert.equal(f.state.queue.length, 0); assert.equal(f.timers.size, 0);
  const missingAck = fixture({ ingest: async () => response(200) }); missingAck.api.error('ignored'); await missingAck.tick(1000);
  assert.equal(missingAck.state.queue.length, 1);
});

test('collector failures never recurse; retry count and backoff are bounded', async () => {
  const f = fixture({ ingest: async () => { throw new TypeError('private sink failure'); } });
  f.api.error('original'); await f.tick(999); assert.equal(f.submissions().length, 0);
  await f.tick(1); assert.equal(f.submissions().length, 1);
  await f.tick(4999); assert.equal(f.submissions().length, 1);
  await f.tick(1); assert.equal(f.submissions().length, 2);
  await f.tick(30000); assert.equal(f.submissions().length, 3);
  await f.tick(300000); assert.equal(f.submissions().length, 3); assert.equal(f.state.queue.length, 0); assert.equal(f.state.events.length, 1);
});

test('even a direct application call to the diagnostics endpoint is excluded from the observer', async () => {
  const f = fixture({ ingest: async () => response(503) });
  await f.window.fetch(endpoint, { method: 'POST' }); assert.equal(f.state.events.length, 0); assert.equal(f.state.queue.length, 0);
});

test('Retry-After is respected and an over-age retry is discarded without a timer', async () => {
  const f = fixture({ ingest: async () => response(429, { headers: { get: () => '120' } }) });
  f.api.error('ignored'); await f.tick(1000); await f.tick(119999); assert.equal(f.submissions().length, 1);
  await f.tick(1); assert.equal(f.submissions().length, 2);
  const excessive = fixture({ ingest: async () => response(429, { headers: { get: () => '3600' } }) });
  excessive.api.error('ignored'); await excessive.tick(1000); assert.equal(excessive.state.queue.length, 0); assert.equal(excessive.timers.size, 0);
});

test('queue, in-memory presentation, deduplication, and per-page submission budget are bounded', async () => {
  const f = fixture({ online: false });
  for (let i = 0; i < 100; i++) f.api.error('private-' + i, { status: 400 + i }, 'network');
  assert.equal(f.state.queue.length, 24); assert.equal(f.state.events.length, 18); assert.equal(f.state.recent.size, 64);
  f.navigator.onLine = true; f.listeners.get('online')(); await f.tick(25000); assert.equal(f.submissions().length, 24);
  for (let i = 0; i < 45; i++) { f.api.error('same'); await f.tick(31000); }
  assert.equal(f.submissions().length, 60); assert.equal(f.state.queue.length, 0);
  const duplicates = fixture(); for (let i = 0; i < 100; i++) duplicates.api.error('different-private-' + i);
  assert.equal(duplicates.state.queue.length, 1); assert.equal(duplicates.state.events[0].sessionCount, 100);
});

test('one actual in-flight request owns delivery despite repeated observations', async () => {
  const pending = deferred(); const f = fixture({ ingest: () => pending.promise });
  f.api.error('one'); await f.tick(1000);
  f.api.error('two', new TypeError('two')); await f.tick(3000);
  assert.equal(f.submissions().length, 1); assert.equal(f.state.queue.length, 2);
  pending.resolve(response(202)); await flush(); await f.tick(1000);
  assert.equal(f.submissions().length, 2); assert.equal(f.state.queue.length, 0);
});

test('page retirement aborts delivery and fences late completion; restored page can collect fresh events', async () => {
  const old = deferred(); let requests = 0;
  const f = fixture({ ingest: () => ++requests === 1 ? old.promise : Promise.resolve(response(202)) });
  f.api.error('one'); await f.tick(1000); const signal = f.submissions()[0].init.signal;
  f.listeners.get('pagehide')(); assert.equal(signal.aborted, true); assert.equal(f.state.queue.length, 0);
  f.listeners.get('pageshow')(); f.api.error('two', new TypeError('two')); await f.tick(1000);
  old.resolve(response(503)); await flush(); await f.tick(100000);
  assert.equal(f.submissions().length, 2); assert.equal(f.state.queue.length, 0); assert.equal(f.state.events.length, 1);
});

test('cleared diagnostic generation cannot be repopulated by an old application request', async () => {
  const old = deferred(); const f = fixture({ fetch: () => old.promise });
  const request = f.window.fetch('/private'); f.api.clearSession(); old.resolve(response(500)); await request;
  assert.equal(f.state.events.length, 0); assert.equal(f.submissions().length, 0);
});

test('missing antiforgery leaves the API usable without anonymous unauthenticated retry traffic', async () => {
  const f = fixture({ metadata: { csrf: '' } }); f.api.error('ignored'); await f.tick(300000);
  assert.equal(f.state.events.length, 1); assert.equal(f.submissions().length, 0); assert.equal(f.state.queue.length, 0);
});

for (const status of [400, 401, 403, 302]) test(`HTTP ${status} permanently stops stale token or redirected delivery on this page`, async () => {
  const f = fixture({ ingest: async () => response(status) }); f.api.error('ignored'); await f.tick(1000);
  f.api.error('new', new TypeError('ignored')); await f.tick(300000);
  assert.equal(f.submissions().length, 1); assert.equal(f.state.disabled, true); assert.equal(f.state.queue.length, 0);
});

test('offline observations remain bounded and wait for online without claiming a software defect', async () => {
  const f = fixture({ online: false, fetch: async () => { throw new TypeError('private transport'); } });
  await assert.rejects(f.window.fetch('/private')); assert.equal(f.state.events[0].payload.errorName, 'OfflineError');
  await f.tick(10000); assert.equal(f.submissions().length, 0);
  f.navigator.onLine = true; f.listeners.get('online')(); await f.tick(1000); assert.equal(f.submissions().length, 1);
});

test('general users never get diagnostic UI or navigation; Founder link uses only the fixed central route', () => {
  const ordinary = fixture(); ordinary.api.error('ignored'); ordinary.api.open(); ordinary.api.close(); assert.deepEqual(ordinary.navigation, []);
  const founder = fixture({ metadata: { founder: 'true' } }); founder.api.open(); assert.deepEqual(founder.navigation, ['/founder/diagnostics']);
});

test('all web hosts retain one early observer; portal navigation is inside the existing Founder group', () => {
  for (const file of ['AgentPortal/Views/Shared/_Layout.cshtml', 'AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml', 'ClientApp/Views/Shared/_Layout.cshtml', 'ParfaitApp/Views/Shared/_Layout.cshtml', 'Protect-Website/Views/Shared/_Layout.cshtml']) {
    const layout = readFileSync(new URL('../../' + file, import.meta.url), 'utf8');
    assert.equal(layout.split('~/Views/Diagnostics/_PageHealth.cshtml').length - 1, 1, file);
    assert.ok(layout.indexOf('~/Views/Diagnostics/_PageHealth.cshtml') < layout.indexOf('@RenderBody()'), file);
  }
  const layout = readFileSync(new URL('../../AgentPortal/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
  const founderGroup = layout.slice(layout.indexOf('@if (isFounder)', layout.indexOf('explore-panel')));
  assert.match(founderGroup, /LegendConnect[\s\S]*href="\/founder\/diagnostics"/);
  assert.doesNotMatch(layout, /explorePageHealth|LegendPageHealth\?\.current.open/);
  const partial = readFileSync(new URL('../../SHARED/Views/Diagnostics/_PageHealth.cshtml', import.meta.url), 'utf8');
  assert.match(partial, /GetAndStoreTokens\(Context\)/); assert.doesNotMatch(partial, /AgentPortal\./);
});
