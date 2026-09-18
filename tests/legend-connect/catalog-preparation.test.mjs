import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/legend-connect.js', import.meta.url), 'utf8');
function fixture(payloads) {
    const element = () => ({ textContent: '', children: [], listeners: {}, setAttribute() {}, append(value) { this.children.push(value); }, replaceChildren(...values) { this.children = values; }, addEventListener(name, callback) { this.listeners[name] = callback; } });
    const status = element(), rows = element(), inspect = element(), prepare = element();
    const controls = { '[data-catalog-status]': status, '[data-catalog-results]': rows, '[data-catalog-inspect]': inspect, '[data-catalog-prepare]': prepare, '[name="__RequestVerificationToken"]': { value: 'fixture-csrf' } };
    const panel = { querySelector: name => controls[name], querySelectorAll: () => [inspect, prepare] };
    const calls = [];
    const context = { document: { querySelector: () => panel, createElement: element }, Date, AbortController, FormData, setTimeout, clearTimeout,
        fetch: async (url, options) => { calls.push({ url, options }); const payload = payloads.shift(); assert.notEqual(payload, undefined); return { ok: true, headers: { get: () => 'application/json' }, json: async () => payload }; } };
    vm.runInNewContext(source.slice(source.indexOf('    const read ='), source.indexOf('    const formatNumber')), context);
    return { status, calls, inspect: () => inspect.listeners.click(), prepare: () => prepare.listeners.click() };
}
const done = version => ({ catalogVersion: version, isComplete: true, completed: 20, total: 20, failures: [], continuation: { disposition: 'Complete' } });
test('inventory uses GET only and never reports an empty inventory complete', async () => {
    const f = fixture([[]]); await f.inspect();
    assert.match(f.status.textContent, /completion is not verified/);
    assert.equal(f.calls.length, 1); assert.notEqual(f.calls[0].options.method, 'POST');
});
test('mixed catalog versions cannot report complete', async () => {
    const f = fixture([[{ code: 'es' }, { code: 'fr' }], done('a'), done('b')]); await f.inspect();
    assert.match(f.status.textContent, /changed during this pass/);
    assert(f.calls.every(call => call.options.method !== 'POST'));
});
test('capacity block ends preparation without retry or false success and includes antiforgery', async () => {
    const f = fixture([[{ code: 'fr' }], { ...done('a'), isComplete: false, completed: 1, failures: [{ code: 'translation_capacity_monthly_exhausted', count: 19 }], continuation: { disposition: 'Blocked', retryAfterSeconds: 600 } }]);
    await f.prepare();
    assert.equal(f.calls.length, 2);
    assert.equal(f.calls[1].options.method, 'POST');
    assert.equal(f.calls[1].options.body.get('__RequestVerificationToken'), 'fixture-csrf');
    assert.match(f.status.textContent, /remain blocked/);
});
