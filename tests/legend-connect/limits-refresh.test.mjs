import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/legend-connect.js', import.meta.url), 'utf8');
function fixture() {
    const listeners = {}, modalListeners = {}, pending = [], intervals = [], additions = [];
    let markup = 'existing cards', replacements = 0;
    const body = {
        scrollTop: 147, scrollLeft: 0, setAttribute() {},
        querySelector: () => null, querySelectorAll: () => [],
        prepend: element => additions.push(element),
        get innerHTML() { return markup; },
        set innerHTML(value) { markup = value; replacements++; }
    };
    const modal = { dataset: {}, addEventListener: (type, fn) => modalListeners[type] = fn };
    const document = {
        getElementById: () => modal,
        querySelector: selector => selector === '[data-translation-limits-body]' ? body : null,
        addEventListener: (type, fn) => (listeners[type] ||= []).push(fn),
        dispatchEvent() {},
        createElement: () => ({ dataset: {}, setAttribute() {}, remove() {} })
    };
    const context = {
        document, window: { setTimeout: () => 1, clearTimeout() {}, setInterval: fn => intervals.push(fn) },
        root: { querySelectorAll: () => [], append() {} },
        location: { search: '' }, URLSearchParams, AbortController, DOMException,
        connectModals: new Set(), parents: new WeakMap(), suspendedParents: new WeakSet(),
        CustomEvent: class { constructor(type) { this.type = type; } },
        fetch: (url, options) => new Promise((resolve, reject) => pending.push({ url, options, resolve, reject }))
    };
    vm.createContext(context);
    vm.runInContext(source.slice(source.indexOf('    const limitsModal ='), source.indexOf('    // Bootstrap supports one visible dialog.')), context);
    const settle = () => new Promise(resolve => setImmediate(resolve));
    return {
        body, additions, intervals, pending, replacements: () => replacements,
        open: () => modalListeners['show.bs.modal'](),
        refresh() { for (const fn of listeners.click) fn({ target: { closest: selector => selector === '[data-limits-retry]' ? {} : null } }); },
        async success(index, html = 'loaded cards') { pending[index].resolve({ ok: true, redirected: false, text: async () => html }); await settle(); },
        async fail(index) { pending[index].reject(new Error('network unavailable')); await settle(); }
    };
}
test('limits refresh is manual after initial load and reopening retains management state', async () => {
    const f = fixture();
    f.open(); assert.equal(f.pending.length, 1);
    await f.success(0);
    f.open(); assert.equal(f.pending.length, 1);
    assert.equal(f.intervals.length, 0);
    f.refresh(); assert.equal(f.pending.length, 2);
    assert.equal(f.body.innerHTML, 'loaded cards');
    assert.equal(f.replacements(), 1);
    await f.success(1, 'updated cards');
    assert.equal(f.body.innerHTML, 'updated cards');
    assert.equal(f.body.scrollTop, 147);
});
test('a failed manual refresh retains loaded cards and exposes a retryable error', async () => {
    const f = fixture(); f.open(); await f.success(0);
    f.refresh(); await f.fail(1);
    assert.equal(f.body.innerHTML, 'loaded cards');
    assert.equal(f.replacements(), 1);
    assert(f.additions.some(node => node.dataset.limitsError === 'true' && node.innerHTML.includes('role="alert"')));
});
