import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Legend-Design/legend-public-cms.js', import.meta.url), 'utf8');

function fixture({ context, origin = 'https://protect.example.test', search = '', denied = false } = {}) {
  const ids = new Map(), events = new Map(), calls = [], alerts = [], errors = [];
  class Element {
    constructor(tag = 'div') {
      this.tagName = tag.toUpperCase(); this.dataset = {}; this.children = [];
      this.textContent = ''; this.listeners = new Map();
      this.style = { setProperty: (key, value) => { this.style[key] = value; } };
      this.classList = { add() {}, remove() {} };
    }
    appendChild(child) { this.children.push(child); child.parentElement = this; return child; }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    closest() { return null; }
    matches() { return false; }
    getAttribute() { return null; }
    querySelectorAll() { return this.children; }
    async click() { await this.listeners.get('click')?.({ target: this }); }
    set innerHTML(html) {
      this.html = html;
      for (const match of html.matchAll(/<([a-z0-9]+)\b[^>]*\bid="([^"]+)"[^>]*>/gi)) {
        const child = new Element(match[1]); ids.set(match[2], child); this.appendChild(child);
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
      elements: { 'home.title': { text } }, theme: { gold: '#123456' }
    } }) };
  };
  const environment = vm.createContext({
    window: { LEGEND_PUBLIC_CMS_CONTEXT: context }, document, fetch,
    location: { origin, pathname: '/', search, href: origin + '/' + search },
    URL, URLSearchParams, HTMLElement: Element, HTMLImageElement: class extends Element {},
    CSS: { escape: value => value }, alert: value => alerts.push(value), console: { error: (...values) => errors.push(values) }
  });
  vm.runInContext(source, environment);
  return { calls, alerts, errors, document, heading, ids, events,
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
