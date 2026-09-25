import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Legend-Design/legend-public-cms.js', import.meta.url), 'utf8');
const publicCss = readFileSync(new URL('../../Legend-Design/legend-public-web.css', import.meta.url), 'utf8');
const businessBuildSource = readFileSync(new URL('../../Legend-Website/scripts/build.mjs', import.meta.url), 'utf8');
const publicInquirySource = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const editorContractsSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs', import.meta.url), 'utf8');
const businessRenderSource = readFileSync(new URL('../../Legend-Website/scripts/render-business.mjs', import.meta.url), 'utf8');
const businessMiddlewareSource = readFileSync(new URL('../../Protect-Website/Services/BusinessWebsiteMiddleware.cs', import.meta.url), 'utf8');
const motionCatalogFixture = {
  triggers:[{key:'load',label:'Page load'},{key:'enter-view',label:'Enter viewport'},{key:'hover',label:'Hover'},{key:'click',label:'Click'}],
  effects:[{key:'fade',label:'Fade'},{key:'slide',label:'Slide'},{key:'scale',label:'Scale'},{key:'rotate',label:'Rotate'},{key:'blur',label:'Blur'}],
  easings:[{key:'linear',label:'Linear'},{key:'ease',label:'Ease'},{key:'ease-in',label:'Ease in'},{key:'ease-out',label:'Ease out'},{key:'ease-in-out',label:'Ease in/out'}],
  directions:[{key:'up',label:'Up'},{key:'down',label:'Down'},{key:'left',label:'Left'},{key:'right',label:'Right'}],
  maxInteractionsPerElement:8
};

const componentCatalogFixture = [
  {type:'text',label:'Text',group:'Basic',inlineText:true,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']},
  {type:'heading',label:'Heading',group:'Basic',inlineText:true,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']},
  {type:'quote',label:'Quote',group:'Basic',inlineText:true,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']},
  {type:'divider',label:'Divider',group:'Basic',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']},
  {type:'spacer',label:'Spacer',group:'Layout',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']},
  {type:'shape',label:'Shape',group:'Design',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed','click']},
  {type:'container',label:'Container',group:'Layout',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:true,layoutModes:['flow','grid','flex','stack','free'],triggers:['viewed']},
  {type:'image',label:'Image',group:'Media',inlineText:false,supportsMedia:true,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed','click']},
  {type:'button',label:'Button / link',group:'Basic',inlineText:true,supportsMedia:false,supportsAction:true,canContainChildren:false,layoutModes:['flow'],triggers:['viewed','click']},
  {type:'video',label:'Video',group:'Media',inlineText:false,supportsMedia:true,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed','click']},
  {type:'card',label:'Card',group:'Layout',inlineText:true,supportsMedia:false,supportsAction:false,canContainChildren:true,layoutModes:['flow','grid','flex','stack'],triggers:['viewed','click']},
  {type:'group',label:'Group',group:'Layout',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:true,layoutModes:['flow','grid','flex','stack','free'],triggers:['viewed']},
  {type:'section',label:'Section',group:'Layout',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:true,layoutModes:['flow','grid','flex','stack','free'],triggers:['viewed','scroll_threshold']},
  {type:'code',label:'Code / embed',group:'Advanced',inlineText:false,supportsMedia:false,supportsAction:false,canContainChildren:false,layoutModes:['flow'],triggers:['viewed']}
];

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
    return { ok: true, status: 200, json: async () => ({ revision: 'revision-one', business: { id:'business-id',displayName:'Fixture business' }, componentCatalog:{options:componentCatalogFixture}, document: init.body ? JSON.parse(init.body).document : {
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
    async editText(value) {
      heading.textContent = value;
      await heading.listeners.get('beforeinput')?.({ target: heading });
      await heading.listeners.get('input')?.({ target: heading });
    },
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
    assert.equal(f.heading.attributes.contenteditable, 'plaintext-only');
    assert.equal(f.ids.get('legend-cms-scale').value, '1');
    assert.equal(f.ids.get('legend-cms-width').value, '72');
    assert.equal(f.ids.get('legend-cms-padding-top').value, '24');
    assert.equal(f.ids.get('legend-cms-padding-bottom').value, '32');
    assert.equal(f.ids.get('legend-cms-align').value, 'center');
    assert.equal(f.ids.get('legend-cms-image-group').hidden, true);
    assert.equal(f.ids.get('legend-cms-inline-help').hidden, false);
    await f.editText('An edited heading');
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
  await f.editText('New title');
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
  assert.equal(f.heading.style.maxWidth, '');
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
  assert.equal(f.ids.get('legend-cms-inline-help').hidden, true);
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
async function domFixture({siteKey='legend',doc={},denied=false,search='?legendEdit=ticket',business=null,pages=[],ctaCatalog=[],componentCatalog=componentCatalogFixture,motionCatalog=motionCatalogFixture,mediaAssets=[],innerWidth=1280,reduceMotion=false,html='<!doctype html><html><head><style>h1{font-size:64px}section{padding:24px}</style></head><body data-page-key="home"><main><section><h1>Template title</h1><a href="https://old.example"><span>Original link</span></a><img src="https://images.example/a.png" alt="original"></section><section><h2>Second section</h2></section></main></body></html>'}={}) {
  const dom = new JSDOM(html, {url:'https://site.example/'+search,runScripts:'outside-only'});
  const {window:w}=dom; const calls=[];
  Object.defineProperty(w,'innerWidth',{value:innerWidth,writable:true,configurable:true});
  const animations=[];
  w.matchMedia=()=>({matches:reduceMotion,addEventListener(){},removeEventListener(){}});
  w.Element.prototype.animate=function(keyframes,options){animations.push({element:this,keyframes,options});return {cancel(){}};};
  w.LEGEND_PUBLIC_CMS_CONTEXT={siteKey,apiBase:'',businessId: business?.id || '',pages};
  w.HTMLDialogElement.prototype.showModal = function() {}; w.HTMLDialogElement.prototype.close = function() { this.dispatchEvent(new w.Event('close')); };
  w.CSS={escape: v=>String(v).replaceAll('"','\\"')}; w.alert=()=>{}; w.confirm=()=>true;
  w.fetch=async(url,init={})=> {
    const href=String(url); calls.push({url:href,...init});
    const parsed=new URL(href,w.location.origin);
    if(parsed.pathname==='/api/website-content/manage/media' && (!init.method || init.method==='GET'))
      return {ok:!denied,status:denied?401:200,json:async()=>({assets:mediaAssets})};
    const body=typeof init.body==='string'?JSON.parse(init.body):null;
    return {ok:!denied,status:denied?401:200,json:async()=>({siteKey,business,revision:'r'+calls.length,document:body?.document || doc,ctaCatalog:{options:ctaCatalog},componentCatalog:{options:componentCatalog},motionCatalog})};
  };
  w.eval(source);
  // JSDOM dispatches initial readiness itself; wait for the fetch continuation.
  await new Promise(resolve=>setTimeout(resolve,0));
  const click=(selector,init={})=>w.document.querySelector(selector).dispatchEvent(new w.MouseEvent('click',{bubbles:true,cancelable:true,...init}));
  const input=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const change=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('change',{bubbles:true}));};
  const editSelected=(value)=> {const el=w.document.querySelector('.legend-cms-selected');assert.ok(el);el.textContent=value;el.dispatchEvent(new w.Event('beforeinput',{bubbles:true,cancelable:true}));el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const save=async()=>{click('#legend-cms-save');input('#legend-cms-draft-name','Test variation');click('#legend-cms-draft-submit');await new Promise(resolve=>setTimeout(resolve,0));return JSON.parse(calls.at(-1).body).document;};
  return {w,calls,animations,click,input,change,editSelected,save,close:()=>w.close()};
}
test('canonical public stylesheet preserves authored spaces, tabs and line breaks',()=>{
  assert.match(publicCss,/\[data-cms-preserve-whitespace="true"\]\{white-space:pre-wrap;tab-size:4;overflow-wrap:anywhere\}/);
});

test('Add panel is rendered from the authenticated component capability catalog',async()=>{
  const f=await domFixture();
  try {
    assert.equal(f.w.document.querySelectorAll('#legend-cms-add-components [data-add]').length, componentCatalogFixture.filter(x=>x.type!=='image').length);
    assert.equal(f.w.document.querySelector('#legend-cms-new-image')?.textContent,'Image');
    assert.equal(source.includes('<button data-add="text">Text</button>'),false);
    assert.ok(editorContractsSource.includes('WebsiteComponentCatalog'));
  } finally { f.close(); }
});

test('professional primitive components come from the shared registry and persist through the same Extras model',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    for (const type of ['heading','quote','divider','spacer','shape','container']) f.click(`[data-add="${type}"]`);
    saved=await f.save();
    const types=new Set(saved.pages['/'].extras.map(item=>item.type));
    for (const type of ['heading','quote','divider','spacer','shape','container']) assert.equal(types.has(type),true);
    const container=saved.pages['/'].extras.find(item=>item.type==='container');
    assert.equal(container.layout.mode,'flow');
    assert.equal(container.style.minHeightPx,160);
    const spacer=saved.pages['/'].extras.find(item=>item.type==='spacer');
    assert.equal(spacer.style.heightPx,48);
    const shape=saved.pages['/'].extras.find(item=>item.type==='shape');
    assert.equal(shape.style.widthPercent,25);
  } finally { f.close(); }
});

test('Motion inspector persists typed interactions from the server catalog and previews them without page-specific code',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.click('[data-open="motion"]');
    f.click('#legend-cms-motion-controls button:last-child');
    const preview=[...f.w.document.querySelectorAll('#legend-cms-motion-controls button')].find(button=>button.textContent==='Preview');
    assert.ok(preview); preview.click();
    assert.equal(f.animations.length,1);
    saved=await f.save();
    const interaction=Object.values(saved.pages['/'].elements).find(item=>item.interactions?.length)?.interactions?.[0];
    assert.ok(interaction);
    assert.equal(interaction.trigger,'load');
    assert.equal(interaction.effect,'fade');
    assert.equal(interaction.durationMs,500);
  } finally { f.close(); }
});

test('public typed load motion plays once and responsive refresh does not rebind the same definition',async()=>{
  const doc={pages:{'/':{elements:{'home.h1.template-title.1':{interactions:[{id:'fade-one',trigger:'load',effect:'fade',durationMs:400,delayMs:0,easing:'ease-out',once:true}]}},sectionOrder:{},extras:[]}}};
  const f=await domFixture({doc,search:''});
  try {
    await new Promise(resolve=>setTimeout(resolve,5));
    assert.equal(f.animations.length,1);
    f.w.dispatchEvent(new f.w.Event('resize'));
    await new Promise(resolve=>setTimeout(resolve,5));
    assert.equal(f.animations.length,1);
    assert.equal(f.animations[0].options.duration,400);
  } finally { f.close(); }
});

test('reduced motion suppresses public playback while preserving the configured interaction',async()=>{
  const doc={pages:{'/':{elements:{'home.h1.template-title.1':{interactions:[{id:'slide-one',trigger:'load',effect:'slide',durationMs:500,delayMs:0,easing:'ease-out',once:true,direction:'up',distancePx:40}]}},sectionOrder:{},extras:[]}}};
  const f=await domFixture({doc,search:'',reduceMotion:true});
  try {
    await new Promise(resolve=>setTimeout(resolve,5));
    assert.equal(f.animations.length,0);
  } finally { f.close(); }
});

test('Media Library reuses existing owner assets through the canonical Extra without a second asset record',async()=>{
  const asset={id:'11111111-1111-1111-1111-111111111111',url:'https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111',contentType:'image/png',sizeBytes:2048,sourceName:'hero.png',createdUtc:'2026-09-25T00:00:00Z'};
  const f=await domFixture({mediaAssets:[asset]}); let saved;
  try {
    f.click('main h1');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(f.w.document.querySelector('#legend-cms-media-status').textContent,/1 of 1 asset/);
    assert.equal(f.w.document.querySelector('.legend-cms-media-card strong').textContent,'hero.png');
    assert.match(f.w.document.querySelector('.legend-cms-media-preview').src,/ticket/);
    f.click('.legend-cms-media-card button');
    saved=await f.save();
    const extra=saved.pages['/'].extras.find(item=>item.type==='image');
    assert.ok(extra);
    assert.equal(extra.imageDataUrl,asset.url);
    assert.equal(extra.alt,'hero.png');
    assert.equal(f.calls.filter(call=>new URL(call.url).pathname==='/api/website-content/manage/media').length,1);
  } finally { f.close(); }
});

test('Media Library replaces a selected image through its existing canonical override',async()=>{
  const asset={id:'22222222-2222-2222-2222-222222222222',url:'https://site.example/api/website-content/media/22222222-2222-2222-2222-222222222222',contentType:'image/webp',sizeBytes:4096,sourceName:'replacement.webp',createdUtc:'2026-09-25T00:00:00Z'};
  const f=await domFixture({mediaAssets:[asset]}); let saved;
  try {
    f.click('main img');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    f.click('.legend-cms-media-card button');
    saved=await f.save();
    const replacement=Object.values(saved.pages['/'].elements).find(item=>item.imageDataUrl===asset.url);
    assert.ok(replacement);
    assert.equal(saved.pages['/'].extras.filter(item=>item.type==='image').length,0);
  } finally { f.close(); }
});

test('mobile edits stay in the responsive variant while desktop base remains unchanged',async()=>{
  const doc={breakpoints:[{id:'tablet',label:'Tablet',maxWidthPx:1024},{id:'mobile',label:'Mobile',maxWidthPx:640}],pages:{'/':{elements:{'home.h1.template-title.1':{style:{widthPercent:80}}},sectionOrder:{},extras:[]}}};
  const f=await domFixture({doc,innerWidth:1280}); let saved;
  try {
    f.click('main h1');
    assert.equal(f.w.document.querySelector('main h1').style.width,'80%');
    f.change('#legend-cms-breakpoint','mobile');
    f.input('#legend-cms-width','45');
    saved=await f.save();
    const element=saved.pages['/'].elements['home.h1.template-title.1'];
    assert.equal(element.style.widthPercent,80);
    assert.equal(element.responsive.mobile.style.widthPercent,45);
    assert.equal(element.responsive.tablet,undefined);
  } finally { f.close(); }

  const desktop=await domFixture({doc:saved,search:'',innerWidth:1280});
  try { assert.equal(desktop.w.document.querySelector('main h1').style.width,'80%'); }
  finally { desktop.close(); }
  const mobile=await domFixture({doc:saved,search:'',innerWidth:500});
  try { assert.equal(mobile.w.document.querySelector('main h1').style.width,'45%'); }
  finally { mobile.close(); }
});

test('responsive variants cascade desktop to tablet to mobile without duplicating complete layouts',async()=>{
  const doc={breakpoints:[{id:'tablet',label:'Tablet',maxWidthPx:1024},{id:'mobile',label:'Mobile',maxWidthPx:640}],pages:{'/':{elements:{'home.h1.template-title.1':{
    style:{widthPercent:90,paddingTop:10},
    responsive:{tablet:{style:{widthPercent:70},layout:{}},mobile:{style:{paddingTop:30},layout:{}}}
  }},sectionOrder:{},extras:[]}}};
  const f=await domFixture({doc,search:'',innerWidth:500});
  try {
    const heading=f.w.document.querySelector('main h1');
    assert.equal(heading.style.width,'70%');
    assert.equal(heading.style.paddingTop,'30px');
  } finally { f.close(); }
});

test('container layout modes are persisted in the same element override and rendered directly',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.click('#legend-cms-container');
    f.input('#legend-cms-layout-mode','grid');
    f.input('#legend-cms-layout-columns','6');
    f.input('#legend-cms-layout-column-gap','18');
    saved=await f.save();
    const section=saved.pages['/'].elements['section:home.section.1'];
    assert.equal(section.layout.mode,'grid');
    assert.equal(section.layout.columns,6);
    assert.equal(section.layout.columnGap,18);
  } finally { f.close(); }

  const published=await domFixture({doc:saved,search:''});
  try {
    const section=published.w.document.querySelector('main section');
    assert.equal(section.style.display,'grid');
    assert.equal(section.style.gridTemplateColumns,'repeat(6, minmax(0, 1fr))');
    assert.equal(section.style.columnGap,'18px');
  } finally { published.close(); }
});

test('professional geometry controls persist at the active breakpoint only',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.change('#legend-cms-breakpoint','tablet');
    const set=(key,value)=>f.input(`[data-geometry-key="${key}"]`,value);
    set('minWidthPx','240');set('maxWidthPx','760');set('aspectRatio','1.5');
    set('opacity','0.72');set('rotationDeg','8');set('scaleX','1.1');set('scaleY','0.9');set('zIndex','12');
    saved=await f.save();
    const element=Object.values(saved.pages['/'].elements).find(x=>x.responsive?.tablet);
    assert.ok(element);
    assert.equal(element.style.minWidthPx,undefined);
    assert.equal(element.responsive.tablet.style.maxWidthPx,760);
    assert.equal(element.responsive.tablet.style.rotationDeg,8);
    assert.equal(element.responsive.tablet.style.zIndex,12);
  } finally { f.close(); }
});


test('editor lock and z-order persist in the same selected element record',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.click('#legend-cms-lock');
    assert.equal(f.w.document.querySelector('main h1').dataset.cmsLocked,'true');
    assert.equal(f.w.document.querySelector('#legend-cms-lock').textContent,'Unlock selected');
    f.click('[data-open="layout"]');
    f.click('[data-z-action="front"]');
    saved=await f.save();
    const element=Object.values(saved.pages['/'].elements).find(x=>x.editorLocked);
    assert.ok(element);
    assert.equal(element.editorLocked,true);
    assert.equal(element.style.zIndex,1);
  } finally { f.close(); }
});

test('modifier multi-select groups through canonical placements and ungroup restores the parent container',async()=>{
  const f=await domFixture(); let grouped;
  try {
    f.click('main h1');
    f.click('main a',{shiftKey:true});
    assert.equal(f.w.document.querySelector('.legend-cms-selection-frame').dataset.multiSelected,'true');
    assert.equal(f.w.document.querySelector('#legend-cms-group-selection').disabled,false);
    f.click('#legend-cms-group-selection');
    grouped=await f.save();
    const group=grouped.pages['/'].extras.find(item=>item.type==='group');
    assert.ok(group);
    const children=Object.values(grouped.pages['/'].elements).filter(item=>item.placement?.containerId===`extra:${group.id}`);
    assert.equal(children.length,2);
    assert.equal(group.layout.mode,'flow');

    f.click('#legend-cms-ungroup-selection');
    const ungrouped=await f.save();
    assert.equal(ungrouped.pages['/'].extras.some(item=>item.type==='group'),false);
    const restored=Object.values(ungrouped.pages['/'].elements).filter(item=>item.placement?.containerId?.startsWith('section:'));
    assert.equal(restored.length>=2,true);
  } finally { f.close(); }
});

test('align selection writes breakpoint-aware absolute positions only inside Free Canvas',async()=>{
  const f=await domFixture(); let saved;
  try {
    const section=f.w.document.querySelector('main section');
    const heading=f.w.document.querySelector('main h1');
    const link=f.w.document.querySelector('main a');
    section.getBoundingClientRect=()=>({left:0,top:0,width:1000,height:500,right:1000,bottom:500});
    heading.getBoundingClientRect=()=>({left:100,top:100,width:200,height:60,right:300,bottom:160});
    link.getBoundingClientRect=()=>({left:320,top:180,width:140,height:40,right:460,bottom:220});

    f.click('main h1');
    f.click('#legend-cms-container');
    f.input('#legend-cms-layout-mode','free');
    f.click('main h1');
    f.click('main a',{shiftKey:true});
    assert.equal(f.w.document.querySelector('[data-align-selection="left"]').disabled,false);
    f.click('[data-align-selection="left"]');
    saved=await f.save();

    const positioned=Object.values(saved.pages['/'].elements).filter(item=>item.style?.positionMode==='absolute');
    assert.equal(positioned.length,2);
    assert.ok(positioned.every(item=>item.style.offsetXPercent===10));
  } finally { f.close(); }
});

test('drag marquee uses the same transient multi-selection and ignores nested interactive text',async()=>{
  const f=await domFixture();
  try {
    const preview=f.w.document.querySelector('.legend-cms-preview');
    const heading=f.w.document.querySelector('main h1');
    const link=f.w.document.querySelector('main a');
    const span=f.w.document.querySelector('main a span');
    preview.getBoundingClientRect=()=>({left:0,top:0,width:1000,height:700,right:1000,bottom:700});
    heading.getBoundingClientRect=()=>({left:100,top:100,width:200,height:60,right:300,bottom:160});
    link.getBoundingClientRect=()=>({left:340,top:120,width:180,height:50,right:520,bottom:170});
    span.getBoundingClientRect=()=>({left:350,top:130,width:100,height:20,right:450,bottom:150});

    preview.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,button:0,clientX:50,clientY:50}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:560,clientY:220}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:560,clientY:220}));

    assert.equal(f.w.document.querySelector('.legend-cms-selection-frame').dataset.multiSelected,'true');
    assert.equal(f.w.document.querySelector('#legend-cms-group-selection').disabled,false);
    assert.equal(span.classList.contains('legend-cms-multi-selected'),false);
  } finally { f.close(); }
});

test('multi-delete hides template elements and removes added groups without orphan placements',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('main h1');
    f.click('main a',{shiftKey:true});
    f.click('#legend-cms-group-selection');
    const group=f.w.document.querySelector('.cms-extra-group');
    assert.ok(group);
    f.click('main h1',{shiftKey:true});
    // Select the group itself only, then delete it; its template descendants must not retain the removed container.
    f.click('.cms-extra-group');
    f.click('#legend-cms-remove');
    saved=await f.save();
    assert.equal(saved.pages['/'].extras.some(item=>item.type==='group'),false);
    const orphaned=Object.values(saved.pages['/'].elements).filter(item=>item.placement?.containerId?.startsWith('extra:'));
    assert.equal(orphaned.length,0);
  } finally { f.close(); }
});

test('professional keyboard shortcuts select siblings group ungroup delete and nudge only in Free Canvas',async()=>{
  const f=await domFixture(); let saved;
  try {
    const section=f.w.document.querySelector('main section');
    const heading=f.w.document.querySelector('main h1');
    const link=f.w.document.querySelector('main a');
    section.getBoundingClientRect=()=>({left:0,top:0,width:1000,height:500,right:1000,bottom:500});
    heading.getBoundingClientRect=()=>({left:100,top:100,width:200,height:60,right:300,bottom:160});
    link.getBoundingClientRect=()=>({left:340,top:120,width:180,height:50,right:520,bottom:170});

    f.click('main h1');
    f.click('#legend-cms-container');
    f.input('#legend-cms-layout-mode','free');
    f.click('main h1');

    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'a',metaKey:true,bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.legend-cms-selection-frame').dataset.multiSelected,'true');

    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'g',metaKey:true,bubbles:true,cancelable:true}));
    assert.ok(f.w.document.querySelector('.cms-extra-group'));
    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'g',metaKey:true,shiftKey:true,bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.cms-extra-group'),null);

    f.click('main h1');
    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'ArrowRight',shiftKey:true,bubbles:true,cancelable:true}));
    saved=await f.save();
    const moved=Object.values(saved.pages['/'].elements).find(item=>item.style?.positionMode==='absolute');
    assert.ok(moved);
    assert.equal(moved.style.offsetXPercent,11);

    f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.legend-cms-selection-frame').hidden,true);
  } finally { f.close(); }
});

test('responsive anchors pin elements to edges center and stretch from the canonical style record',async()=>{
  const doc={breakpoints:[{id:'tablet',label:'Tablet',maxWidthPx:1024},{id:'mobile',label:'Mobile',maxWidthPx:640}],pages:{'/':{elements:{'home.h1.template-title.1':{
    style:{positionMode:'absolute',horizontalAnchor:'right',verticalAnchor:'bottom',insetRightPx:24,insetBottomPx:36,rotationDeg:5},
    responsive:{mobile:{style:{horizontalAnchor:'center',verticalAnchor:'top',insetTopPx:18},layout:{}}}
  }},sectionOrder:{},extras:[]}}};
  const desktop=await domFixture({doc,search:'',innerWidth:1280});
  try {
    const heading=desktop.w.document.querySelector('main h1');
    assert.equal(heading.style.position,'absolute');
    assert.equal(heading.style.right,'24px');
    assert.equal(heading.style.bottom,'36px');
    assert.equal(heading.style.left,'');
    assert.match(heading.style.transform,/rotate\(5deg\)/);
  } finally { desktop.close(); }

  const mobile=await domFixture({doc,search:'',innerWidth:500});
  try {
    const heading=mobile.w.document.querySelector('main h1');
    assert.equal(heading.style.left,'50%');
    assert.equal(heading.style.top,'18px');
    assert.equal(heading.style.right,'');
    assert.match(heading.style.transform,/translateX\(-50%\)/);
    assert.match(heading.style.transform,/rotate\(5deg\)/);
  } finally { mobile.close(); }
});

test('custom breakpoint manager persists document breakpoints without a parallel preference store',async()=>{
  const f=await domFixture(); let saved;
  try {
    f.click('#legend-cms-manage-breakpoints');
    const dialog=f.w.document.querySelector('#legend-cms-breakpoint-dialog');
    assert.ok(dialog);
    const add=[...dialog.querySelectorAll('button')].find(button=>button.textContent==='Add breakpoint');
    assert.ok(add); add.click();
    const rows=dialog.querySelectorAll('.legend-cms-breakpoint-row');
    assert.equal(rows.length,3);
    const last=rows[rows.length-1];
    const inputs=last.querySelectorAll('input');
    inputs[0].value='Wide mobile';
    inputs[0].dispatchEvent(new f.w.Event('input',{bubbles:true}));
    inputs[1].value='520';
    inputs[1].dispatchEvent(new f.w.Event('input',{bubbles:true}));
    const saveButton=[...dialog.querySelectorAll('button')].find(button=>button.textContent==='Save breakpoints');
    saveButton.click();
    saved=await f.save();
    assert.equal(saved.breakpoints.length,3);
    assert.ok(saved.breakpoints.some(item=>item.label==='Wide mobile'&&item.maxWidthPx===520));
  } finally { f.close(); }
});

test('removing a breakpoint prunes its responsive overrides throughout the canonical document',async()=>{
  const doc={breakpoints:[{id:'tablet',label:'Tablet',maxWidthPx:1024},{id:'mobile',label:'Mobile',maxWidthPx:640}],pages:{'/':{elements:{'home.h1.template-title.1':{responsive:{mobile:{style:{widthPercent:44},layout:{}}}}},sectionOrder:{},extras:[]}}};
  const f=await domFixture({doc}); let saved;
  try {
    f.click('#legend-cms-manage-breakpoints');
    const dialog=f.w.document.querySelector('#legend-cms-breakpoint-dialog');
    const rows=dialog.querySelectorAll('.legend-cms-breakpoint-row');
    const mobileRow=[...rows].find(row=>row.querySelector('input')?.value==='Mobile');
    assert.ok(mobileRow);
    [...mobileRow.querySelectorAll('button')].find(button=>button.textContent==='Remove').click();
    [...dialog.querySelectorAll('button')].find(button=>button.textContent==='Save breakpoints').click();
    saved=await f.save();
    assert.equal(saved.breakpoints.some(item=>item.id==='mobile'),false);
    assert.equal(saved.pages['/'].elements['home.h1.template-title.1'].responsive?.mobile,undefined);
  } finally { f.close(); }
});

test('shared business inquiry uses Protect contact identity and two-column rows',()=>{
  assert.ok(businessBuildSource.includes('id="website_inquiry"'));
  for (const field of ['FirstName','LastName','Phone','Email']) assert.ok(businessBuildSource.includes(`name="${field}"`));
  assert.ok(publicInquirySource.includes("fields.get('FirstName')"));
  assert.ok(publicInquirySource.includes("fields.get('LastName')"));
  assert.ok(publicInquirySource.includes("fields.get('Phone')"));
  assert.ok(publicInquirySource.includes("fields.get('Email')"));
  assert.ok(source.includes("requiredContactFields: inquiryForm ? ['FirstName','LastName','Phone','Email'] : []"));
  assert.ok(publicCss.includes('.public-form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr))'));
});

test('Founder and business websites use one shared inquiry runtime with no hard-coded founder email form path',()=>{
  assert.ok(businessBuildSource.includes('function publicInquiryForm'));
  assert.ok(businessBuildSource.includes('data-website-inquiry data-form-key="website_inquiry"'));
  assert.ok(businessBuildSource.includes('${publicInquiryForm()}</section>'));
  assert.ok(businessBuildSource.includes('publicInquiryForm({preview:true,business:true})'));
  assert.ok(businessBuildSource.includes('/legend-public-inquiry.js?v='));
  assert.equal(businessBuildSource.includes('mailto:connect@mylegnd.com'),false);
  assert.equal(editorContractsSource.includes('legend_email'),false);
  assert.ok(publicInquirySource.includes("new URLSearchParams(location.search).has('legendEdit')"));
  assert.ok(publicInquirySource.includes("document.querySelectorAll('[data-website-inquiry]:not([data-preview])')"));
  assert.ok(publicInquirySource.includes("new URL('/api/website-inquiries/public', apiBase)"));
});

test('published business rendering activates the shared inquiry path without injecting a second runtime',()=>{
  assert.ok(businessRenderSource.includes("doc.querySelector('[data-website-inquiry]')"));
  assert.ok(businessRenderSource.includes("form.removeAttribute('data-preview')"));
  assert.ok(businessRenderSource.includes("form.querySelectorAll('[disabled]').forEach(element=>element.removeAttribute('disabled'))"));
  assert.ok(businessRenderSource.includes("form.querySelector('[data-preview-notice]')?.remove()"));
  assert.equal(businessRenderSource.includes("script.src='/business-inquiry.js'"),false);
});

test('custom code blocks use opaque data frames instead of weakening the page script policy',()=>{
  assert.ok(source.includes("frame.src = 'data:text/html;charset=utf-8,' + encodeURIComponent(source)"));
  assert.equal(source.includes('allow-same-origin'),false);
  assert.ok(businessMiddlewareSource.includes("frame-src data:; object-src 'none'"));
  const policy=businessMiddlewareSource.match(/default-src 'self'; script-src[^"]+/)?.[0] || '';
  assert.equal(policy.includes("script-src 'self' 'unsafe-inline'"),false);
  assert.equal(policy.includes("script-src 'self' 'unsafe-eval'"),false);
});

test('shared mobile navigation opens as compact horizontal button grids',()=>{
  assert.ok(publicCss.includes('.nav[data-open=true]{display:grid}'));
  assert.ok(publicCss.includes('grid-template-columns:repeat(4,minmax(0,1fr))'));
  assert.ok(publicCss.includes('.nav{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}'));
  assert.equal(publicCss.includes('.nav[data-open=true]{display:flex}'),false);
});
for (const siteKey of ['legend','protect','business']) {
  test(`${siteKey}: shared editor round-trips authored whitespace exactly`,async()=>{
    const business=siteKey==='business'?{id:'business-id',displayName:'Fixture business'}:null;
    const f=await domFixture({siteKey,business}); let saved;
    const value='Line one\n\tLine two  with  spaces';
    try {
      f.click('main h1'); f.editSelected(value);
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
test('shared CTA dropdown creates a styled live button and keeps its label independently editable',async()=>{
  const actions=[
    {key:'business_contact',group:'Contact',label:'Contact form',defaultText:'Contact Us',href:'/contact',openInNewTab:false},
    {key:'business_call',group:'Contact',label:'Call the business',defaultText:'Call Now',href:'tel:+16025550199',openInNewTab:false}
  ];
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},ctaCatalog:actions});
  try {
    f.click('main h1'); f.click('[data-add="button"]');
    const button=f.w.document.querySelector('[data-cms-extra-id]');
    assert.ok(button.classList.contains('primary'));
    assert.equal(button.getAttribute('href'),'/contact');
    assert.equal(button.textContent,'Contact Us');
    f.change('#legend-cms-action','business_call');
    assert.equal(button.getAttribute('href'),'tel:+16025550199');
    assert.equal(button.textContent,'Call Now');
    f.editSelected('Talk with our team');
    assert.equal(button.textContent,'Talk with our team');
    const saved=await f.save(); const extra=saved.pages['/'].extras[0];
    assert.equal(extra.actionKey,'business_call');
    assert.equal(extra.href,'tel:+16025550199');
    assert.equal(extra.text,'Talk with our team');
  } finally { f.close(); }
});
test('manual destination remains available and intentionally leaves managed CTA routing',async()=>{
  const actions=[{key:'legend_contact',group:'Contact',label:'Contact LEGEND®',defaultText:'Contact Us',href:'/contact',openInNewTab:false}];
  const f=await domFixture({ctaCatalog:actions});
  try {
    f.click('main h1'); f.click('[data-add="button"]');
    f.input('#legend-cms-href','https://example.com/custom');
    const saved=await f.save(); const extra=saved.pages['/'].extras[0];
    assert.equal(extra.actionKey,undefined);
    assert.equal(extra.href,'https://example.com/custom');
  } finally { f.close(); }
});

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
      f.click('main a span'); f.editSelected('Book a visit'); f.input('#legend-cms-href','https://business.example/book');
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
    f.editSelected('Persisted before domain');
    await new Promise(resolve=>setTimeout(resolve,1000));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    assert.equal(JSON.parse(saveCall.body).document.pages['/'].elements[Object.keys(JSON.parse(saveCall.body).document.pages['/'].elements)[0]].text,'Persisted before domain');
    const toggle=f.w.document.querySelector('#legend-cms-panel-toggle');
    assert.ok(toggle);
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),true);
    assert.equal(toggle.textContent,'Open controls');
    assert.ok(f.w.document.querySelector('.legend-cms-selected'));
    f.editSelected('Still editing full width');
    assert.equal(f.w.document.querySelector('main h1').textContent,'Still editing full width');
    f.click('#legend-cms-panel-toggle');
    assert.equal(f.w.document.body.classList.contains('legend-cms-panel-hidden'),false);
    assert.equal(toggle.textContent,'Full-page canvas');
  } finally { f.close(); }
});
test('section deletion preserves child structure, reset restores, global theme remains separate',async()=>{
  const f=await domFixture();try {f.click('main h1');f.click('#legend-cms-container'); f.click('#legend-cms-remove');
    assert.equal(f.w.document.querySelector('main section').hidden,true);assert.ok(f.w.document.querySelector('main section h1'));
    const doc=await f.save();assert.equal(doc.pages['/'].elements['section:home.section.1'].hidden,true);
  }finally{f.close();}
});
test('direct canvas replaces designated drop controls and persists shared geometry',async()=>{
  const f=await domFixture();let saved;try {
    f.click('main h1');
    assert.equal(f.w.document.querySelector('#legend-cms-destination'),null);
    assert.equal(f.w.document.querySelector('#legend-cms-column'),null);
    assert.equal(f.w.document.querySelector('#legend-cms-place'),null);
    assert.ok(f.w.document.querySelector('.legend-cms-selection-frame'));
    assert.ok(f.w.document.querySelector('.legend-cms-grid-overlay'));
    assert.equal(f.w.document.querySelectorAll('[data-cms-gesture]').length,4);
    f.input('#legend-cms-width','50');
    f.input('#legend-cms-height','240');
    f.input('#legend-cms-offset-x','25');
    f.input('#legend-cms-offset-y','48');
    saved=await f.save();
    const style=Object.values(saved.pages['/'].elements)[0].style;
    assert.equal(style.widthPercent,50);assert.equal(style.heightPx,240);assert.equal(style.offsetXPercent,25);assert.equal(style.offsetYPx,48);
  }finally{f.close();}
  const loaded=await domFixture({doc:saved,search:''});try {
    const heading=loaded.w.document.querySelector('main h1');
    assert.equal(heading.style.width,'50%');assert.equal(heading.style.height,'240px');assert.equal(heading.style.left,'25%');assert.equal(heading.style.top,'48px');
    assert.equal(loaded.w.document.querySelector('.legend-cms-panel'),null);
  }finally{loaded.close();}
});
test('publish saves unsaved draft first then calls the explicit publish action',async()=>{
 const f=await domFixture();try{f.click('main h1');f.editSelected('New draft');f.click('#legend-cms-publish');await new Promise(r=>setTimeout(r,0));assert.equal(f.calls.length,3);assert.ok(f.calls[1].url.endsWith('/manage'));assert.ok(f.calls[2].url.endsWith('/manage/publish'));assert.equal(JSON.parse(f.calls[2].body).expectedRevision,'r2');}finally{f.close();}
});
test('editing current page preserves independent page content',async()=>{
 const f=await domFixture({doc:{pages:{about:{title:'About',elements:{'about.h1':{text:'Other page'}},extras:[],sectionOrder:{}}}}});try{f.click('main h1');f.editSelected('Home edit');const doc=await f.save();assert.equal(doc.pages['/about'].elements['about.h1'].text,'Other page');assert.ok(doc.pages['/']);}finally{f.close();}
});
test('undo and redo restore content and leave other page drafts intact',async()=>{
 const f=await domFixture();try{f.click('main h1');f.editSelected('First edit');f.editSelected('Second edit');f.click('#legend-cms-undo');assert.equal(f.w.document.querySelector('main h1').textContent,'First edit');f.click('#legend-cms-redo');assert.equal(f.w.document.querySelector('main h1').textContent,'Second edit');}finally{f.close();}
});
test('selected blocks disable legacy HTML drag and expose snap-grid handles',async()=>{
 const f=await domFixture();try {
  f.click('main h1');
  const heading=f.w.document.querySelector('main h1');
  assert.equal(heading.draggable,false);
  assert.equal(f.w.document.querySelector('.legend-cms-move-handle').getAttribute('title'),'Move on grid');
  assert.match(source,/background-size:calc\(100% \/ 12\) 100%,100% 24px/);
  assert.equal(f.w.document.querySelector('#legend-cms-drag'),null);
 }finally{f.close();}
});

test('sandboxed code block saves source and previews without same-origin access',async()=>{
 const f=await domFixture();try {
  f.click('main h1');f.click('[data-add="code"]');
  const dialog=f.w.document.querySelector('.legend-cms-code-dialog');
  assert.ok(dialog);
  const sourceInput=dialog.querySelector('.legend-cms-code-source');
  sourceInput.value='<style>body{margin:0}</style><form><input aria-label="Custom field"></form><script>document.body.dataset.ready="1"<\/script>';
  f.click('.legend-cms-code-actions button');
  const block=f.w.document.querySelector('.cms-extra-code');
  const frame=block.querySelector('iframe');
  assert.ok(frame.src.startsWith('data:text/html;charset=utf-8,'));
  assert.equal(decodeURIComponent(frame.src.slice(frame.src.indexOf(',')+1)),sourceInput.value);
  assert.equal(frame.getAttribute('sandbox').includes('allow-same-origin'),false);
  const saved=await f.save();const extra=saved.pages['/'].extras.find(x=>x.type==='code');
  assert.ok(extra);assert.equal(extra.text,sourceInput.value);assert.equal(extra.style.widthPercent,100);assert.equal(extra.style.heightPx,320);
 }finally{f.close();}
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
      assert.equal(f.w.document.querySelectorAll('.legend-cms-tabs [data-open]').length, 7);
      assert.equal(f.w.document.querySelector('[data-open="signals"]'), null);
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
    f.editSelected('Another booking link');
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


test('scoped favicon is projected from the canonical website document without leaking editor tickets',async()=>{
  const media='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
  const html='<!doctype html><html><head><link rel="icon" href="/favicon.svg" type="image/svg+xml"></head><body data-page-key="home"><main><section><h1>Title</h1></section></main></body></html>';
  const f=await domFixture({doc:{faviconImageDataUrl:media},html});
  try {
    const link=f.w.document.querySelector('link[rel~="icon"]');
    assert.equal(new URL(link.href).pathname,'/api/website-content/media/11111111-1111-1111-1111-111111111111');
    assert.equal(new URL(link.href).searchParams.get('ticket'),'ticket');
    assert.equal(link.hasAttribute('type'),false);
    const saved=await f.save();
    assert.equal(saved.faviconImageDataUrl,media);
    assert.equal(saved.faviconImageDataUrl.includes('ticket='),false);
  } finally { f.close(); }
});

test('removing a scoped favicon restores the canonical fallback before publication',async()=>{
  const media='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
  const html='<!doctype html><html><head><link rel="icon" href="/favicon.svg" type="image/svg+xml"></head><body data-page-key="home"><main><section><h1>Title</h1></section></main></body></html>';
  const f=await domFixture({doc:{faviconImageDataUrl:media},html});
  try {
    f.click('#legend-cms-favicon-remove');
    const link=f.w.document.querySelector('link[rel~="icon"]');
    assert.equal(link.getAttribute('href'),'/favicon.svg');
    assert.equal(link.getAttribute('type'),'image/svg+xml');
    const saved=await f.save();
    assert.equal(saved.faviconImageDataUrl,null);
  } finally { f.close(); }
});
