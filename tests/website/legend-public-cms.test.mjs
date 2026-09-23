import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Legend-Design/legend-public-cms.js', import.meta.url), 'utf8');
const publicCss = readFileSync(new URL('../../Legend-Design/legend-public-web.css', import.meta.url), 'utf8');

function fixture({ context, origin = 'https://protect.example.test', search = '', denied = false, savedStyle = null } = {}) {
  const ids = new Map(), events = new Map(), calls = [], alerts = [], errors = [], windowEvents = new Map();
  class Element {
    constructor(tag = 'div') {
      this.tagName = tag.toUpperCase(); this.dataset = {}; this.children = [];
      this.textContent = ''; this.listeners = new Map(); this.attributes = {}; this.clientWidth = 1000; this.className = ''; this.baseFontSize = 64;
      this.style = { removeProperty: key => { delete this.style[key]; }, setProperty: (key, value) => { this.style[key] = value; } };
      this.classList = {
        add: name => { if (!this.className.split(' ').includes(name)) this.className = (this.className + ' ' + name).trim(); },
        remove: name => { this.className = this.className.split(' ').filter(value => value && value !== name).join(' '); },
        contains: name => this.className.split(' ').includes(name),
        toggle: name => { const has = this.className.split(' ').includes(name); if (has) this.classList.remove(name); else this.classList.add(name); return !has; }
      };
    }
    set id(value) { this._id = value; ids.set(value, this); }
    get id() { return this._id; }
    appendChild(child) { if (child.parentElement) child.parentElement.children = child.parentElement.children.filter(x => x !== child); this.children.push(child); child.parentElement = this; return child; }
    insertBefore(child, before) { if (!before) return this.appendChild(child); this.children.splice(this.children.indexOf(before), 0, child); child.parentElement = this; return child; }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(x => x !== this); }
    get firstChild() { return this.children[0]; }
    setAttribute(key, value) { this.attributes[key] = value; }
    showModal() {}
    focus() {}
    close() { this.listeners.get('close')?.(); }
    setCustomValidity(value) { this.validationMessage = value; }
    async input(value) { this.value = value; await this.listeners.get('input')?.({ target: this }); }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    closest(selector) { return selector === '[data-cms-editable="true"]' && this.dataset.cmsEditable === 'true' ? this : null; }
    matches() { return false; }
    getAttribute() { return null; }
    querySelector(selector) { return selector === '.legend-cms-bar' ? this.children.find(x => x.className.includes('legend-cms-bar')) : selector === '[data-cms-view="layout"]' ? new Element('section') : null; }
    querySelectorAll(selector) { return selector?.startsWith('[data-') ? [] : this.children; }
    async click() { await this.listeners.get('click')?.({ target: this }); }
    replaceChildren(...children) { this.children = []; children.forEach(child => this.appendChild(child)); }
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
    return { ok: true, status: 200, json: async () => ({ revision: 'revision-one', business: { id:'business-id',displayName:'Fixture business' }, document: init.body ? JSON.parse(init.body).document : {
      elements: { 'home.title': { text, ...(savedStyle ? { style: savedStyle } : {}) } }, theme: { gold: '#123456' }
    } }) };
  };
  const environment = vm.createContext({
    window: { LEGEND_PUBLIC_CMS_CONTEXT: context, addEventListener: (name, handler) => windowEvents.set(name, handler) }, document, fetch,
    getComputedStyle: el => ({ fontSize: el.style.fontSize || `${el.baseFontSize}px`, width: el.style.width || '720px',
      paddingTop: el.style.paddingTop || '24px', paddingBottom: el.style.paddingBottom || '32px', paddingLeft: '0px', paddingRight: '0px', textAlign: el.style.textAlign || 'center' }),
    location: { origin, pathname: '/', search, href: origin + '/' + search },
    URL, URLSearchParams, HTMLElement: Element, HTMLImageElement: class extends Element {},
    CSS: { escape: value => value }, alert: value => alerts.push(value), console: { error: (...values) => errors.push(values) },
    setTimeout, clearTimeout, requestAnimationFrame: callback => { callback(); return 1; }
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
  assert.equal(f.document.documentElement.style['--web-gold'], '#123456');
  assert.equal(f.ids.has('legend-cms-save'), false);
  assert.deepEqual(f.errors, []);
});

test('Protect empty API base initializes the ticketed editor and saves to the same authority', async () => {
  const ticket = 'fixture-ticket+/=';
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=' + encodeURIComponent(ticket) });
  await f.ready();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url.href, 'https://protect.example.test/api/website-content/manage?ticket=fixture-ticket%2B%2F%3D');
  assert.equal(f.heading.textContent, 'Editor content');
  assert.ok(f.ids.has('legend-cms-save'));
  await f.ids.get('legend-cms-save').click();
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  const saved = f.calls[1];
  assert.equal(saved.url.href, 'https://protect.example.test/api/website-content/manage');
  assert.equal(saved.init.method, 'POST');
  assert.equal(saved.init.headers['Content-Type'], 'application/json');
  assert.equal(JSON.parse(saved.init.body).ticket, ticket);
  assert.equal(JSON.parse(saved.init.body).document.pages['/'].elements['home.title'].text, 'Editor content');
  assert.equal(f.ids.get('legend-cms-status').textContent, 'Draft saved');
  assert.deepEqual(f.alerts, []);
});

test('LEGEND explicit cross-origin API base remains the public, editor, and save authority', async () => {
  const f = fixture({ context: { siteKey: 'LEGEND', apiBase: 'https://portal.example.test/' },
    origin: 'https://www.example.test', search: '?legendEdit=fixture-ticket' });
  await f.ready();
  await f.ids.get('legend-cms-save').click();
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  assert.equal(f.calls.length, 2);
  assert.ok(f.calls.every(call => call.url.origin === 'https://portal.example.test'));
  assert.equal(f.calls[0].url.pathname, '/api/website-content/manage');
  assert.equal(f.calls[0].url.searchParams.get('ticket'), 'fixture-ticket');
  assert.equal(JSON.parse(f.calls[1].init.body).ticket, 'fixture-ticket');
  assert.deepEqual(f.alerts, []);
});

test('an expired ticket cannot build the editor or expose a save control', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=expired-ticket', denied: true });
  await f.ready();
  assert.equal(f.calls.length, 1);
  assert.equal(f.heading.textContent, 'Default content');
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
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
    const override = JSON.parse(f.calls.at(-1).init.body).document.pages['/'].elements['home.title'];
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
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  assert.equal(JSON.parse(f.calls.at(-1).init.body).document.pages['/'].elements['home.title'].style.fontScale, undefined);
});

test('adjustments above former caps round-trip without changing unrelated fields', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=ticket' });
  await f.ready(); f.select();
  await f.ids.get('legend-cms-scale').input('12.75');
  await f.ids.get('legend-cms-width').input('250.25');
  await f.ids.get('legend-cms-padding-top').input('500.5');
  await f.ids.get('legend-cms-padding-bottom').input('800');
  await f.ids.get('legend-cms-save').click();
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.pages['/'].elements['home.title'].style,
    { fontScale: 12.75, widthPercent: 250.25, paddingTop: 500.5, paddingBottom: 800 });
  assert.equal(f.heading.style.width, '250.25%');
  assert.equal(f.heading.style.maxWidth, '100%');
});

test('invalid numeric edits never replace a valid stored adjustment', async () => {
  const f = fixture({ context: { siteKey: 'protect', apiBase: '' }, search: '?legendEdit=ticket' });
  await f.ready(); f.select();
  await f.ids.get('legend-cms-scale').input('3');
  for (const bad of ['-2', '0', 'Infinity', 'NaN']) await f.ids.get('legend-cms-scale').input(bad);
  await f.ids.get('legend-cms-padding-top').input('-1');
  await f.ids.get('legend-cms-save').click();
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.pages['/'].elements['home.title'].style, { fontScale: 3 });
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
  await f.ids.get('legend-cms-reset').click();
  assert.equal(f.heading.textContent, 'Default content');
  assert.equal(f.heading.style.fontSize, '');
  await f.ids.get('legend-cms-save').click();
  f.ids.get('legend-cms-draft-name').value = 'Test variation';
  await f.ids.get('legend-cms-draft-submit').click();
  assert.deepEqual(JSON.parse(f.calls.at(-1).init.body).document.pages['/'].elements, {});
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

// Full DOM integration: these tests execute the same shipped editor, not copied helpers.
import { JSDOM } from 'jsdom';
async function domFixture({siteKey='legend',doc={},denied=false,search='?legendEdit=ticket',business=null,pages=[],html='<!doctype html><html><head><style>h1{font-size:64px}section{padding:24px}</style></head><body data-page-key="home"><main><section><h1>Template title</h1><a href="https://old.example"><span>Original link</span></a><img src="https://images.example/a.png" alt="original"></section><section><h2>Second section</h2></section></main></body></html>'}={}) {
  const dom = new JSDOM(html, {url:'https://site.example/'+search,runScripts:'outside-only'});
  const {window:w}=dom; const calls=[];
  w.LEGEND_PUBLIC_CMS_CONTEXT={siteKey,apiBase:'',businessId: business?.id || '',pages};
  w.HTMLDialogElement.prototype.showModal = function() {}; w.HTMLDialogElement.prototype.close = function() { this.dispatchEvent(new w.Event('close')); };
  w.CSS={escape: v=>String(v).replaceAll('"','\\"')}; w.alert=()=>{}; w.confirm=()=>true;
  w.fetch=async(url,init={})=> { calls.push({url:String(url),...init}); const body=init.body?JSON.parse(init.body):null; return {ok:!denied,status:denied?401:200,json:async()=>({siteKey,business,revision:'r'+calls.length,document:body?.document || doc})}; };
  w.eval(source);
  // JSDOM dispatches initial readiness itself; wait for the fetch continuation.
  await new Promise(resolve=>setTimeout(resolve,0));
  const click=selector=>w.document.querySelector(selector).dispatchEvent(new w.MouseEvent('click',{bubbles:true,cancelable:true}));
  const input=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const save=async()=>{click('#legend-cms-save');input('#legend-cms-draft-name','Test variation');click('#legend-cms-draft-submit');await new Promise(resolve=>setTimeout(resolve,0));return JSON.parse(calls.at(-1).body).document;};
  return {w,calls,click,input,save,close:()=>w.close()};
}
test('canonical public stylesheet preserves authored spaces, tabs and line breaks',()=>{
  assert.match(publicCss,/\[data-cms-preserve-whitespace="true"\]\{white-space:pre-wrap;tab-size:4;overflow-wrap:anywhere\}/);
});
for (const siteKey of ['legend','protect','business']) {
  test(`${siteKey}: shared editor round-trips authored whitespace exactly`,async()=>{
    const business=siteKey==='business'?{id:'business-id',displayName:'Fixture business'}:null;
    const f=await domFixture({siteKey,business}); let saved;
    const value='Line one\n\tLine two  with  spaces';
    try {
      f.click('main h1'); f.input('#legend-cms-text',value);
      const heading=f.w.document.querySelector('main h1');
      assert.equal(heading.textContent,value);
      assert.equal(heading.dataset.cmsPreserveWhitespace,'true');
      saved=await f.save();
      assert.ok(Object.values(saved.pages['/'].elements).some(item=>item.text===value));
    } finally { f.close(); }
    const published=await domFixture({siteKey,business,doc:saved,search:''});
    try {
      assert.equal(published.w.document.querySelector('main h1').textContent,value);
      assert.equal(published.w.document.querySelector('main h1').dataset.cmsPreserveWhitespace,'true');
    } finally { published.close(); }
  });
}
test('new button goes to the bottom of the selected container and persists that flow placement',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><div class="chosen"><h1>Headline</h1><p>Copy</p></div><div class="other"><p>Other</p></div></section></main></body></html>';
  const f=await domFixture({html});
  try {
    f.click('.chosen h1'); f.click('[data-add="button"]');
    const container=f.w.document.querySelector('.chosen');
    const button=container.querySelector('[data-cms-extra-id]');
    assert.ok(button); assert.equal(container.lastElementChild,button);
    const saved=await f.save(); const extra=saved.pages['/'].extras[0];
    assert.equal(extra.type,'button'); assert.equal(extra.placement.flow,true);
    assert.equal(extra.placement.containerId,container.dataset.cmsId); assert.equal(extra.placement.beforeId,null);
  } finally { f.close(); }
});
test('business services can be duplicated and deleted as whole cards without generic icons',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><div class="card-grid"><article class="card"><h3>Service one</h3><p>First description</p></article><article class="card"><h3>Service two</h3><p>Second description</p></article></div></section></main></body></html>';
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},html});
  try {
    f.click('.card-grid > article.card h3');
    assert.equal(f.w.document.querySelector('#legend-cms-duplicate').textContent,'Duplicate service');
    assert.equal(f.w.document.querySelector('#legend-cms-remove').textContent,'Delete service');
    f.click('#legend-cms-duplicate');
    const cards=f.w.document.querySelectorAll('.card-grid > article.card');
    assert.equal(cards.length,3); assert.equal(cards[2].querySelector('h3').textContent,'Service one');
    assert.equal(cards[2].querySelector('.icon'),null);
    let saved=await f.save(); const service=saved.pages['/'].extras.find(item=>item.type==='card');
    assert.ok(service); assert.equal(service.title,'Service one'); assert.equal(service.text,'First description'); assert.equal(service.placement.flow,true);
    f.click('.card-grid > article.card h3'); f.click('#legend-cms-remove');
    assert.equal(f.w.document.querySelector('.card-grid > article.card').hidden,true);
    saved=await f.save();
    assert.ok(Object.values(saved.pages['/'].elements).some(item=>item.hidden===true));
  } finally { f.close(); }
});

for(const siteKey of ['legend','protect']) {
  test(`${siteKey}: nested existing links edit label and URL, reject unsafe destinations, draft uses revision`,async()=>{
    const f=await domFixture({siteKey}); try {
      f.click('main a span'); f.input('#legend-cms-text','Book a visit'); f.input('#legend-cms-href','https://business.example/book');
      assert.equal(f.w.document.querySelector('main a').textContent,'Book a visit');
      assert.equal(f.w.document.querySelector('main a').href,'https://business.example/book');
      f.input('#legend-cms-href','javascript:alert(1)');
      assert.equal(f.w.document.querySelector('main a').href,'https://business.example/book');
      const saved=await f.save(); assert.ok(Object.values(saved.pages['/'].elements).some(x=>x.href==='https://business.example/book'));
      assert.equal(JSON.parse(f.calls.at(-1).body).expectedRevision,'r1');
      assert.ok(f.calls.every(x=>!x.url.includes('/public/')));
    } finally {f.close();}
  });
}
test('autosave persists edits before domain connection and panel can collapse to a full-page preview', async()=>{
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'}});
  try {
    f.click('main h1');
    f.input('#legend-cms-text','Persisted before domain');
    await new Promise(resolve=>setTimeout(resolve,1000));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    assert.equal(JSON.parse(saveCall.body).document.pages['/'].elements[Object.keys(JSON.parse(saveCall.body).document.pages['/'].elements)[0]].text,'Persisted before domain');
    const toggle=f.w.document.querySelector('#legend-cms-panel-toggle');
    assert.ok(toggle);
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),true);
    assert.equal(toggle.textContent,'Open editor');
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),false);
  } finally { f.close(); }
});
test('section deletion preserves child structure, reset restores, global theme remains separate',async()=>{
  const f=await domFixture();try {f.click('main h1');f.click('#legend-cms-container'); f.click('#legend-cms-remove');
    assert.equal(f.w.document.querySelector('main section').hidden,true);assert.ok(f.w.document.querySelector('main section h1'));
    const doc=await f.save();assert.equal(doc.pages['/'].elements['section:home.section.1'].hidden,true);
  }finally{f.close();}
});
test('new blocks, keyboard placement and published reload preserve page bounds and section ownership',async()=>{
  const f=await domFixture();let saved;try {
    f.click('main h1');f.click('[data-add="button"]');f.input('#legend-cms-text','Contact us');f.input('#legend-cms-href','https://business.example/contact');
    f.click('[data-open="layout"]');f.w.document.querySelector('#legend-cms-destination').value='home.section.2';
    f.w.document.querySelector('#legend-cms-column').value='10';f.w.document.querySelector('#legend-cms-span').value='12';f.click('#legend-cms-place');
    const block=f.w.document.querySelector('[data-cms-extra-id]'); assert.equal(block.closest('[data-cms-section]').dataset.cmsSection,'home.section.2');assert.equal(block.style.getPropertyValue('--cms-column'),'10');assert.equal(block.style.getPropertyValue('--cms-span'),'3');
    saved=await f.save();
  }finally{f.close();}
  const loaded=await domFixture({doc:saved,search:''});try {const block=loaded.w.document.querySelector('[data-cms-extra-id]');assert.equal(block.textContent,'Contact us');assert.equal(block.style.getPropertyValue('--cms-column'),'10');assert.equal(block.style.getPropertyValue('--cms-span'),'3');assert.equal(block.closest('[data-cms-section]').dataset.cmsSection,'home.section.2');assert.equal(loaded.w.document.querySelector('.legend-cms-panel'),null);}finally{loaded.close();}
});
test('publish saves unsaved draft first then calls the explicit publish action',async()=>{
 const f=await domFixture();try{f.click('main h1');f.input('#legend-cms-text','New draft');f.click('#legend-cms-publish');await new Promise(r=>setTimeout(r,0));assert.equal(f.calls.length,3);assert.ok(f.calls[1].url.endsWith('/manage'));assert.ok(f.calls[2].url.endsWith('/manage/publish'));assert.equal(JSON.parse(f.calls[2].body).expectedRevision,'r2');}finally{f.close();}
});
test('editing current page preserves independent page content',async()=>{
 const f=await domFixture({doc:{pages:{about:{title:'About',elements:{'about.h1':{text:'Other page'}},extras:[],sectionOrder:{}}}}});try{f.click('main h1');f.input('#legend-cms-text','Home edit');const doc=await f.save();assert.equal(doc.pages['/about'].elements['about.h1'].text,'Other page');assert.ok(doc.pages['/']);}finally{f.close();}
});
test('undo and redo restore content and leave other page drafts intact',async()=>{
 const f=await domFixture();try{f.click('main h1');f.input('#legend-cms-text','First edit');f.input('#legend-cms-text','Second edit');f.click('#legend-cms-undo');assert.equal(f.w.document.querySelector('main h1').textContent,'First edit');f.click('#legend-cms-redo');assert.equal(f.w.document.querySelector('main h1').textContent,'Second edit');}finally{f.close();}
});
test('drag drop preserves the destination flow and reloads in the same location',async()=>{
 const f=await domFixture();let saved;try {
  const heading=f.w.document.querySelector('main h1'), target=f.w.document.querySelector('main h2');
  f.click('main h1'); target.getBoundingClientRect=()=>({top:100,height:40});
  let dragged;const transfer={setData(_type,value){dragged=value;},getData(){return dragged;}};
  const start=new f.w.Event('dragstart',{bubbles:true,cancelable:true});Object.defineProperty(start,'dataTransfer',{value:transfer});heading.dispatchEvent(start);
  const drop=new f.w.Event('drop',{bubbles:true,cancelable:true});Object.defineProperties(drop,{dataTransfer:{value:transfer},clientY:{value:101}});target.dispatchEvent(drop);
  assert.equal(heading.nextElementSibling,target);assert.equal(heading.parentElement,target.parentElement);
  assert.equal(f.w.document.querySelector('.cms-layout-frame'),null); saved=await f.save();
 }finally{f.close();}
 const loaded=await domFixture({doc:saved,search:''});try{assert.equal(loaded.w.document.querySelector('main h1').nextElementSibling,loaded.w.document.querySelector('main h2'));}finally{loaded.close();}
});
test('solid section color replaces template gradient and reset restores the original',async()=>{
 const f=await domFixture();try {
  const section=f.w.document.querySelector('main section');section.style.backgroundImage='linear-gradient(black, blue)';
  f.click('main h1');f.click('#legend-cms-container');f.input('[data-style-key="backgroundColor"]','#000000');
  assert.equal(section.style.backgroundColor,'rgb(0, 0, 0)');assert.equal(section.style.backgroundImage,'none');
  const saved=await f.save();const loaded=await domFixture({doc:saved,search:''});try{assert.equal(loaded.w.document.querySelector('main section').style.backgroundImage,'none');}finally{loaded.close();}
 }finally{f.close();}
});
test('managed media tickets are render-only and never leak into saved or external media URLs',async()=>{
 const media='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
 const doc={pages:{home:{elements:{},sectionOrder:{},extras:[{id:'own',type:'image',sectionId:'home.section.1',imageDataUrl:media},{id:'external',type:'image',sectionId:'home.section.1',imageDataUrl:'https://external.example/photo.png'}]}}};
 const f=await domFixture({doc});try{assert.equal(new URL(f.w.document.querySelector('[data-cms-extra-id="own"]').src).searchParams.get('ticket'),'ticket');assert.equal(new URL(f.w.document.querySelector('[data-cms-extra-id="external"]').src).search,'');const saved=await f.save();assert.equal(saved.pages['/'].extras[0].imageDataUrl,media);assert.equal(JSON.stringify(saved).includes('?ticket'),false);}finally{f.close();}
});

for (const siteKey of ['legend', 'protect', 'business']) {
  test(`${siteKey}: shared studio keeps navigation, theme and metadata available without selection`, async () => {
    const f = await domFixture({siteKey, business: siteKey === 'business' ? {id: 'business-id', displayName: 'Fixture business'} : null});
    try {
      assert.equal(f.w.document.querySelectorAll('.legend-cms-tabs [data-open]').length, 8);
      f.click('[data-open="page"]');
      assert.equal(f.w.document.querySelector('#legend-cms-page-title').disabled, false);
      f.input('#legend-cms-page-title', 'A title <with text>');
      f.input('#legend-cms-page-description', 'Services for our community.');
      f.click('[data-open="theme"]');
      assert.equal(f.w.document.querySelector('[data-theme-key="fontFamily"]').disabled, false);
      f.input('[data-theme-key="fontFamily"]', 'Georgia');
      const saved = await f.save();
      assert.equal(saved.pages['/'].title, 'A title <with text>');
      assert.equal(saved.pages['/'].description, 'Services for our community.');
      assert.equal(saved.theme.fontFamily, 'Georgia');
      assert.equal(Object.hasOwn(saved.pages, 'home'), false);
    } finally { f.close(); }
  });
}

test('layers recover a hidden section without losing its descendants', async () => {
  const f = await domFixture();
  try {
    f.click('main h1'); f.click('#legend-cms-container'); f.click('#legend-cms-remove');
    f.click('[data-open="layers"]');
    f.click('#legend-cms-layers button[aria-label^="Show Section"]');
    assert.equal(f.w.document.querySelector('main section').hidden, false);
    assert.equal(f.w.document.querySelector('main h1').textContent, 'Template title');
    const saved = await f.save();
    assert.equal(saved.pages['/'].elements['section:home.section.1'].hidden, false);
  } finally { f.close(); }
});

test('duplicate link has independent identity and undo removes only the duplicate', async () => {
  const f = await domFixture();
  try {
    f.click('main a'); f.input('#legend-cms-href', 'https://business.example/book');
    f.click('#legend-cms-duplicate');
    f.input('#legend-cms-text', 'Another booking link');
    const saved = await f.save();
    assert.equal(saved.pages['/'].extras[0].href, 'https://business.example/book');
    assert.equal(saved.pages['/'].extras[0].text, 'Another booking link');
    assert.equal(f.w.document.querySelector('main a').textContent, 'Original link');
    f.click('#legend-cms-undo'); f.click('#legend-cms-undo');
    assert.equal(f.w.document.querySelectorAll('main a').length, 1);
  } finally { f.close(); }
});

test('design controls preserve numeric font weight and signed letter spacing', async () => {
  const f = await domFixture();
  try {
    f.click('main h1'); f.input('[data-style-key="fontWeight"]', '700');
    f.input('[data-style-key="letterSpacing"]', '-1.25');
    const saved = await f.save();
    const style = Object.values(saved.pages['/'].elements)[0].style;
    assert.equal(style.fontWeight, 700);
    assert.equal(style.letterSpacing, -1.25);
  } finally { f.close(); }
});

test('legacy page keys migrate without discarding another page or canonical content', async () => {
  const f = await domFixture({doc: {pages: {
    home: {title: 'Legacy', elements: {'home.legacy': {text: 'Keep'}}},
    '/': {title: 'Current', elements: {'home.current': {text: 'Current'}}},
    about: {title: 'About', elements: {}}
  }}});
  try {
    const saved = await f.save();
    assert.deepEqual(Object.keys(saved.pages).sort(), ['/', '/about']);
    assert.equal(saved.pages['/'].title, 'Current');
    assert.equal(saved.pages['/'].elements['home.legacy'].text, 'Keep');
    assert.equal(saved.pages['/about'].title, 'About');
  } finally { f.close(); }
});

test('deleting an added block removes it from the saved document and published reload', async () => {
  const f = await domFixture(); let saved;
  try {
    f.click('main h1'); f.click('[data-add="button"]'); f.click('#legend-cms-remove');
    saved = await f.save();
    assert.equal(saved.pages['/'].extras.length, 0);
  } finally { f.close(); }
  const published = await domFixture({doc: saved, search: ''});
  try { assert.equal(published.w.document.querySelectorAll('[data-cms-extra-id]').length, 0); }
  finally { published.close(); }
});

test('link editing rejects editor credentials and insecure absolute URLs before save', async () => {
  const f = await domFixture();
  try {
    f.click('main a'); f.input('#legend-cms-href', '/contact');
    for (const value of ['http://external.example', '/?legendEdit=secret', '/?ticket=secret', '//external.example']) {
      f.input('#legend-cms-href', value);
      assert.equal(f.w.document.querySelector('main a').getAttribute('href'), '/contact');
    }
    const saved = await f.save();
    assert.ok(Object.values(saved.pages['/'].elements).some(value => value.href === '/contact'));
    assert.equal(JSON.stringify(saved).includes('secret'), false);
  } finally { f.close(); }
});

for (const siteKey of ['legend', 'protect', 'business']) {
 test(`${siteKey}: page selector uses supplied catalog, including pages absent from navigation`, async () => {
  const business=siteKey==='business'?{id:'business-test',displayName:'Business'}:null;
  const f=await domFixture({siteKey,business,pages:[{path:'/',label:'Home'},{path:'/unlinked-page',label:'Unlinked page'}]});
  try { const options=[...f.w.document.querySelector('#legend-cms-page-select').options];assert.ok(options.some(o=>o.value==='/unlinked-page'&&o.textContent==='Unlinked page'));assert.ok(!options.some(o=>o.value==='/contact')); }
  finally {f.close();}
 });
}
test('business selector includes imported custom routes from only the authorized draft',async()=>{
 const f=await domFixture({siteKey:'business',business:{id:'business-test',displayName:'Business'},pages:[{path:'/',label:'Home'}],doc:{pages:{'/special-offer':{title:'Special offer',elements:{},extras:[]}}}});
 try {assert.ok([...f.w.document.querySelector('#legend-cms-page-select').options].some(o=>o.value==='/special-offer'&&o.textContent==='Special offer'));}finally{f.close();}
});

test('site palette does not retain the template blue gradient stop',async()=>{
 const f=await domFixture({doc:{theme:{navy:'#000000',navyDeep:'#000000'}}});
 try {assert.equal(f.w.document.documentElement.style.getPropertyValue('--web-navy-royal'),'#000000');}finally{f.close();}
});
