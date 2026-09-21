import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Legend-Design/legend-public-cms.js', import.meta.url), 'utf8');

function fixture({ context, origin = 'https://protect.example.test', search = '', denied = false, savedStyle = null } = {}) {
  const ids = new Map(), events = new Map(), calls = [], alerts = [], errors = [], windowEvents = new Map();
  class Element {
    constructor(tag = 'div') {
      this.tagName = tag.toUpperCase(); this.dataset = {}; this.children = [];
      this.textContent = ''; this.listeners = new Map(); this.attributes = {}; this.clientWidth = 1000; this.className = ''; this.baseFontSize = 64;
      this.style = { setProperty: (key, value) => { this.style[key] = value; } };
      this.classList = { add() {}, remove() {} };
    }
    appendChild(child) { if (child.parentElement) child.parentElement.children = child.parentElement.children.filter(x => x !== child); this.children.push(child); child.parentElement = this; return child; }
    insertBefore(child, before) { if (!before) return this.appendChild(child); this.children.splice(this.children.indexOf(before), 0, child); child.parentElement = this; return child; }
    get firstChild() { return this.children[0]; }
    setAttribute(key, value) { this.attributes[key] = value; }
    setCustomValidity(value) { this.validationMessage = value; }
    async input(value) { this.value = value; await this.listeners.get('input')?.({ target: this }); }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    closest(selector) { return selector === '[data-cms-editable="true"]' && this.dataset.cmsEditable === 'true' ? this : null; }
    matches() { return false; }
    getAttribute() { return null; }
    querySelectorAll() { return this.children; }
    async click() { await this.listeners.get('click')?.({ target: this }); }
    set innerHTML(html) {
      this.html = html;
      for (const match of html.matchAll(/<([a-z0-9]+)\b[^>]*\bid="([^"]+)"[^>]*>/gi)) {
        const child = new Element(match[1]); child.id = match[2]; child.hidden = /\shidden[\s>]/.test(match[0]); ids.set(match[2], child); this.appendChild(child);
      }
    }
  }
  const heading = new Element('h1'); heading.dataset.cmsId = 'home.title'; heading.textContent = 'Default content';
  const main = new Element('main'); main.appendChild(heading);
  const document = {
    body: new Element('body'), head: new Element('head'), documentElement: new Element('html'),
    createElement: tag => new Element(tag), getElementById: id => ids.get(id) || null,
    addEventListener: (name, handler) => events.set(name, handler),
    querySelector: selector => selector === 'main' ? main : selector === '[data-cms-id="home.title"]' ? heading : null,
    querySelectorAll: () => []
  };
  document.body.dataset.pageKey = 'home'; document.body.appendChild(main);
  const fetch = async (input, init = {}) => {
    const url = new URL(String(input)); calls.push({ url, init });
    if (url.pathname.endsWith('/manage') && denied) return { ok: false, status: 401 };
    const text = url.pathname.endsWith('/manage') ? 'Editor content' : 'Published content';
    return { ok: true, status: 200, json: async () => ({ document: init.body ? JSON.parse(init.body).document : {
      elements: { 'home.title': { text, ...(savedStyle ? { style: savedStyle } : {}) } }, theme: { gold: '#123456' }
    } }) };
  };
  const environment = vm.createContext({
    window: { LEGEND_PUBLIC_CMS_CONTEXT: context, addEventListener: (name, handler) => windowEvents.set(name, handler) }, document, fetch,
    getComputedStyle: el => ({ fontSize: el.style.fontSize || `${el.baseFontSize}px`, width: el.style.width || '720px',
      paddingTop: el.style.paddingTop || '24px', paddingBottom: el.style.paddingBottom || '32px', paddingLeft: '0px', paddingRight: '0px', textAlign: el.style.textAlign || 'center' }),
    location: { origin, pathname: '/', search, href: origin + '/' + search },
    URL, URLSearchParams, HTMLElement: Element, HTMLImageElement: class extends Element {},
    CSS: { escape: value => value }, alert: value => alerts.push(value), console: { error: (...values) => errors.push(values) }
  });
  vm.runInContext(source, environment);
  return { calls, alerts, errors, document, heading, ids, events, windowEvents,
    select() { events.get('click')({ target: heading, preventDefault() {}, stopPropagation() {} }); },
    async ready() { await events.get('DOMContentLoaded')?.(); } };
}

test('Protect empty API base loads published content from the current origin', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '', agentSlug: 'agent/example & one' } });
  await f.ready();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url.origin, 'https://protect.example.test');
  assert.equal(f.calls[0].url.pathname, '/api/website-content/public/protect');
  assert.equal(f.calls[0].url.searchParams.get('agentSlug'), 'agent/example & one');
  assert.equal(f.calls[0].url.searchParams.has('ticket'), false);
  assert.equal(f.heading.textContent, 'Published content');
  assert.equal(f.document.documentElement.style['--gold'], '#123456');
  assert.equal(f.ids.has('legend-cms-save'), false);
  assert.deepEqual(f.errors, []);
});

test('Protect empty API base initializes the ticketed editor and saves to the same authority', async () => {
  const ticket = 'fixture-ticket+/=';
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=' + encodeURIComponent(ticket) });
  await f.ready();
  assert.equal(f.calls.length, 2);
  assert.equal(f.calls[1].url.href, 'https://protect.example.test/api/website-content/manage?ticket=fixture-ticket%2B%2F%3D');
  assert.equal(f.heading.textContent, 'Editor content');
  assert.ok(f.ids.has('legend-cms-save'));
  await f.ids.get('legend-cms-save').click();
  const saved = f.calls[2];
  assert.equal(saved.url.href, 'https://protect.example.test/api/website-content/manage');
  assert.equal(saved.init.method, 'POST');
  assert.equal(saved.init.headers['Content-Type'], 'application/json');
  assert.equal(JSON.parse(saved.init.body).ticket, ticket);
  assert.equal(JSON.parse(saved.init.body).document.elements['home.title'].text, 'Editor content');
  assert.equal(f.ids.get('legend-cms-status').textContent, 'Saved live');
  assert.deepEqual(f.alerts, []);
});

test('LEGEND explicit cross-origin API base remains the public, editor, and save authority', async () => {
  const f = fixture({ context: { siteKey: 'LEGEND', apiBase: 'https://portal.example.test/' },
    origin: 'https://www.example.test', search: '?legendEdit=fixture-ticket' });
  await f.ready();
  await f.ids.get('legend-cms-save').click();
  assert.equal(f.calls.length, 3);
  assert.ok(f.calls.every(call => call.url.origin === 'https://portal.example.test'));
  assert.equal(f.calls[0].url.pathname, '/api/website-content/public/legend');
  assert.equal(f.calls[1].url.searchParams.get('ticket'), 'fixture-ticket');
  assert.equal(JSON.parse(f.calls[2].init.body).ticket, 'fixture-ticket');
  assert.deepEqual(f.alerts, []);
});

test('an expired ticket cannot build the editor or expose a save control', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=expired-ticket', denied: true });
  await f.ready();
  assert.equal(f.calls.length, 2);
  assert.equal(f.heading.textContent, 'Published content');
  assert.equal(f.ids.has('legend-cms-save'), false);
  assert.deepEqual(f.alerts, ['This edit session has expired.']);
  assert.equal(f.calls.some(call => call.init.method === 'POST'), false);
});

for (const [name, context] of [
  ['absent context', undefined], ['absent site key', { apiBase: '' }],
  ['absent API setting', { siteKey: 'protect' }], ['invalid API setting', { siteKey: 'protect', apiBase: false }]
]) {
  test(name + ' fails closed without requests or editor initialization', async () => {
    const f = fixture({ context, search: '?legendEdit=fixture-ticket' });
    await f.ready();
    assert.equal(f.events.has('DOMContentLoaded'), false);
    assert.equal(f.calls.length, 0);
    assert.equal(f.ids.has('legend-cms-save'), false);
  });
}

for (const siteKey of ['legend', 'protect']) {
  test(`${siteKey}: selection populates actual defaults and text-only edit preserves original layout`, async () => {
    const f = fixture({ context: { siteKey, apiBase: '' }, search: '?legendEdit=ticket' });
    await f.ready(); f.select();
    assert.equal(f.ids.get('legend-cms-text').value, 'Editor content');
    assert.equal(f.ids.get('legend-cms-scale').value, '1');
    assert.equal(f.ids.get('legend-cms-width').value, '72');
    assert.equal(f.ids.get('legend-cms-padding-top').value, '24');
    assert.equal(f.ids.get('legend-cms-padding-bottom').value, '32');
    assert.equal(f.ids.get('legend-cms-align').value, 'center');
    assert.equal(f.ids.get('legend-cms-image-group').hidden, true);
    assert.equal(f.ids.get('legend-cms-text-group').hidden, false);
    await f.ids.get('legend-cms-text').input('An edited heading');
    await f.ids.get('legend-cms-save').click();
    const override = JSON.parse(f.calls.at(-1).init.body).document.elements['home.title'];
    assert.equal(override.text, 'An edited heading');
    assert.deepEqual(override.style, {});
    assert.equal(f.heading.style.fontSize, '');
    assert.equal(f.heading.style.width, '');
    assert.equal(f.heading.style.paddingTop, '');
  });
}

test('scale is relative to original responsive typography and never accumulates', async () => {
  const f = fixture({ context: { siteKey: 'legend', apiBase: '' }, search: '?legendEdit=ticket', savedStyle: { fontScale: 2 } });
  await f.ready(); f.select();
  assert.equal(f.heading.style.fontSize, '128px');
  await f.ids.get('legend-cms-scale').input('5');
  assert.equal(f.heading.style.fontSize, '320px');
  await f.ids.get('legend-cms-text').input('New title');
  assert.equal(f.heading.style.fontSize, '320px');
  f.heading.baseFontSize = 40;
  f.windowEvents.get('resize')();
  assert.equal(f.heading.style.fontSize, '200px');
  f.windowEvents.get('resize')();
  assert.equal(f.heading.style.fontSize, '200px');
  await f.ids.get('legend-cms-scale').input('');
  assert.equal(f.heading.style.fontSize, '');
  await f.ids.get('legend-cms-save').click();
  assert.equal(JSON.parse(f.calls.at(-1).init.body).document.elements['home.title'].style.fontScale, undefined);
});

test('adjustments above former caps round-trip without changing unrelated fields', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=ticket' });
  await f.ready(); f.select();
  await f.ids.get('legend-cms-scale').input('12.75');
  await f.ids.get('legend-cms-width').input('250.25');
  await f.ids.get('legend-cms-padding-top').input('500.5');
  await f.ids.get('legend-cms-padding-bottom').input('800');
  await f.ids.get('legend-cms-save').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.elements['home.title'].style,
    { fontScale: 12.75, widthPercent: 250.25, paddingTop: 500.5, paddingBottom: 800 });
  assert.equal(f.heading.style.width, '250.25%');
  assert.equal(f.heading.style.maxWidth, 'none');
});

test('invalid numeric edits never replace a valid stored adjustment', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=ticket' });
  await f.ready(); f.select();
  await f.ids.get('legend-cms-scale').input('3');
  for (const bad of ['-2', '0', 'Infinity', 'NaN']) await f.ids.get('legend-cms-scale').input(bad);
  await f.ids.get('legend-cms-padding-top').input('-1');
  await f.ids.get('legend-cms-save').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.elements['home.title'].style, { fontScale: 3 });
  assert.equal(f.heading.style.fontSize, '192px');
});

test('editor places the complete website in its own preview beside the inspector, with toolbar inside', async () => {
  const f = fixture({ context: { siteKey: 'legend', apiBase: '' }, search: '?legendEdit=ticket' });
  await f.ready();
  const preview = f.document.body.children.find(el => el.className === 'legend-cms-preview');
  const panel = f.document.body.children.find(el => el.className.includes('legend-cms-panel'));
  assert.ok(preview.children.includes(f.heading.parentElement));
  assert.equal(panel.firstChild.className, 'legend-cms-editor legend-cms-bar');
  assert.equal(f.ids.get('legend-cms-text-group').hidden, true);
  assert.equal(f.ids.get('legend-cms-image-group').hidden, true);
});

test('reset restores original content and persists removal without discarding unsaved edits', async () => {
  const f = fixture({ context: { siteKey: 'legend', apiBase: '' }, search: '?legendEdit=ticket', savedStyle: { fontScale: 2 } });
  await f.ready(); f.select();
  await f.ids.get('legend-cms-remove').click();
  assert.equal(f.heading.textContent, 'Default content');
  assert.equal(f.heading.style.fontSize, '');
  await f.ids.get('legend-cms-save').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.elements, {});
});

test('public pages retain responsive baseline-relative scale after viewport changes', async () => {
  const f = fixture({ context: { siteKey: 'legend', apiBase: '' }, savedStyle: { fontScale: 3 } });
  await f.ready();
  assert.equal(f.heading.style.fontSize, '192px');
  f.heading.baseFontSize = 24;
  f.windowEvents.get('resize')();
  assert.equal(f.heading.style.fontSize, '72px');
  assert.equal(f.ids.has('legend-cms-save'), false);
});


test('business CMS sends the authoritative business id through the existing public endpoint', async () => {
  const businessId = '5b01f1d0-12f6-4d4c-a263-2ddf11c81318';
  const f = fixture({
    context: { siteKey: 'business', apiBase: 'https://protect.example.test', businessId },
    origin: 'https://www.example.test'
  });
  await f.ready();
  assert.equal(f.calls[0].url.pathname, '/api/website-content/public/business');
  assert.equal(f.calls[0].url.searchParams.get('businessId'), businessId);
});
