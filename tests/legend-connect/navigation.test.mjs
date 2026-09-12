import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/legend-connect.js', import.meta.url), 'utf8');
const view = readFileSync(new URL('../../AgentPortal/Views/LegendConnect/Index.cshtml', import.meta.url), 'utf8');
function fixture() {
    const listeners = { window: {}, document: {} };
    const register = owner => (type, callback) => (listeners[owner][type] ||= []).push(callback);
    const window = { addEventListener: register('window') };
    const document = { addEventListener: register('document'), getElementById: id => modals.get(id) };
    const modals = new Map();
    const calls = [];
    const emit = (modal, type) => {
        const event = { target: modal };
        for (const entry of [...(modal.listeners[type] || [])]) {
            if (entry.once) modal.listeners[type].splice(modal.listeners[type].indexOf(entry), 1);
            entry.callback(event);
        }
        for (const callback of listeners.document[type] || []) callback(event);
    };
    function modal(id) {
        const body = { scrollTop: 187, scrollLeft: 4 };
        const value = { id, body, dataset: {}, visible: false, listeners: {},
            querySelectorAll: () => [body],
            addEventListener(type, callback, options) { (this.listeners[type] ||= []).push({ callback, once: options?.once }); },
            closest() { return this; } };
        modals.set(id, value);
        return value;
    }
    const bootstrap = { Modal: { getOrCreateInstance: element => ({
        show() { calls.push('show:' + element.id); element.visible = true; emit(element, 'show.bs.modal'); emit(element, 'shown.bs.modal'); },
        hide() { calls.push('hide:' + element.id); element.visible = false; emit(element, 'hidden.bs.modal'); }
    }) } };
    window.bootstrap = bootstrap;
    const context = { window, document, bootstrap, WeakMap, WeakSet, Array,
        connectModals: { has: element => modals.has(element.id) }, ownsConnectElement: () => true, refreshMetrics() {} };
    vm.createContext(context);
    vm.runInContext(source.slice(source.indexOf('    const parents = new WeakMap();'), source.indexOf('    if (new URLSearchParams(location.search).get("panel")')), context);
    let bootstrapClicks = 0;
    function click(parent, child) {
        const trigger = { closest: selector => selector === '.modal.show' ? (parent.visible ? parent : null) : null,
            getAttribute: () => '#' + child.id,
            focus: options => { trigger.focusOptions = options; } };
        const event = { target: { closest: () => trigger }, preventDefault() {}, stopImmediatePropagation() { this.stopped = true; } };
        for (const callback of listeners.window.click || []) callback(event);
        // Bootstrap's document capture listener was registered before Connect's.
        if (!event.stopped) { bootstrapClicks++; bootstrap.Modal.getOrCreateInstance(parent).hide(); }
        return trigger;
    }
    return { modal, click, calls, bootstrapClicks: () => bootstrapClicks, close: value => bootstrap.Modal.getOrCreateInstance(value).hide() };
}
test('child editor intercepts before Bootstrap and returns to the retained parent with scroll/focus', () => {
    const f = fixture(), parent = f.modal('translationLimitsModal'), child = f.modal('account');
    parent.visible = true;
    const trigger = f.click(parent, child);
    assert.equal(f.bootstrapClicks(), 0);
    assert.equal(child.visible, true);
    parent.body.scrollTop = 0;
    f.close(child);
    assert.equal(parent.visible, true);
    assert.equal(parent.body.scrollTop, 187);
    assert.equal(trigger.focusOptions.preventScroll, true);
    assert.deepEqual(f.calls, ['hide:translationLimitsModal', 'show:account', 'hide:account', 'show:translationLimitsModal']);
});
test('three-level navigation restores only the immediate parent, then the original management dialog', () => {
    const f = fixture(), outer = f.modal('outer'), middle = f.modal('middle'), inner = f.modal('inner');
    outer.visible = true;
    f.click(outer, middle); f.click(middle, inner);
    assert.equal(outer.visible, false);
    assert.equal(middle.visible, false);
    f.close(inner);
    assert.equal(middle.visible, true);
    assert.equal(outer.visible, false);
    f.close(middle);
    assert.equal(outer.visible, true);
});
test('all thirteen inspection capabilities have direct launchers and retained lazy-load panels', () => {
    const expected = ['submissions','curriculum','candidates','evidence','relationships','learning','machine-learning-lifecycle','research-observability','models','health','retained-knowledge','language-pairs','provider-observations'];
    for (const key of expected) assert(view.includes(`("${key}",`), key);
    assert(view.includes('data-bs-target="#lcInspect-@panel.Item1"'));
    assert(view.includes('data-legend-section="@panel.Item1"'));
    for (const action of ['SubmitKnowledge','SubmitCurriculum','SetCompositionMode','CreateIntelligenceEvaluationSnapshot']) assert(view.includes(`asp-action="${action}"`));
});
test('inspection reopening preserves existing search/pagination and never duplicates an in-flight load', () => {
    let shown, requests = 0;
    const panel = { open: false }, activeRequests = new WeakMap(), panelState = new WeakMap();
    const context = { document: { querySelectorAll: () => [{ addEventListener: (_, fn) => { shown = fn; }, querySelector: () => panel }] },
        activeRequests, panelState, requestPage: value => { requests++; activeRequests.set(value, {}); } };
    vm.createContext(context);
    const start = source.indexOf('    document.querySelectorAll("[data-legend-inspection-modal]")');
    vm.runInContext(source.slice(start, source.indexOf('\n    document\n', start)), context);
    shown(); shown(); assert.equal(requests, 1);
    const retained = { search: 'curriculum', page: 4 };
    panelState.set(panel, retained); activeRequests.delete(panel);
    shown(); assert.equal(requests, 1); assert.equal(panelState.get(panel), retained);
});
