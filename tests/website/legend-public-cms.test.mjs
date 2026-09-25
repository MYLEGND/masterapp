import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Legend-Design/legend-public-cms.js', import.meta.url), 'utf8');
const publicCss = readFileSync(new URL('../../Legend-Design/legend-public-web.css', import.meta.url), 'utf8');
const businessBuildSource = readFileSync(new URL('../../Legend-Website/scripts/build.mjs', import.meta.url), 'utf8');
const publicInquirySource = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const metaSignalSource = readFileSync(new URL('../../Protect-Website/wwwroot/js/meta-signal-intelligence.js', import.meta.url), 'utf8');
const editorContractsSource = readFileSync(new URL('../../Infrastructure/WebsiteEditing/WebsiteEditorContracts.cs', import.meta.url), 'utf8');
const businessRenderSource = readFileSync(new URL('../../Legend-Website/scripts/render-business.mjs', import.meta.url), 'utf8');
const businessMiddlewareSource = readFileSync(new URL('../../Protect-Website/Services/BusinessWebsiteMiddleware.cs', import.meta.url), 'utf8');

function fixture({ context, origin = 'https://protect.example.test', search = '', denied = false, savedStyle = null } = {}) {
  const ids = new Map(), events = new Map(), calls = [], alerts = [], errors = [], windowEvents = new Map();
  class Element {
    constructor(tag = 'div') {
      this.tagName = tag.toUpperCase(); this.dataset = {}; this.children = [];
      this.textContent = ''; this.listeners = new Map(); this.attributes = {}; this.clientWidth = 1000; this.className = ''; this.baseFontSize = 64; this.scrollLeft = 0; this.scrollTop = 0;
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
    get options() { return this.children.filter(child => child.tagName === 'OPTION'); }
    getBoundingClientRect() { return { left:0, top:0, right:this.clientWidth, bottom:40, width:this.clientWidth, height:40 }; }
    scrollIntoView() {}
    setPointerCapture() {}
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
  const request=JSON.parse(f.calls.at(-1).init.body);
  assert.deepEqual(request.document.pages['/'].elements, {});
  assert.ok(request.deletedKeys.includes('page:/|element:home.title'));
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
async function domFixture({siteKey='legend',doc={},denied=false,search='?legendEdit=ticket',pathname='/',business=null,pages=[],ctaCatalog=[],signalCatalog=null,qualityPayload=null,mediaPayload=null,aiPayload=null,signalTestPayload=null,signalHealthPayload=null,collaborationPayload=null,commentPayload=null,viewportWidth=1024,html='<!doctype html><html><head><style>h1{font-size:64px}section{padding:24px}</style></head><body data-page-key="home"><main><section><h1>Template title</h1><a href="https://old.example"><span>Original link</span></a><img src="https://images.example/a.png" alt="original"></section><section><h2>Second section</h2></section></main></body></html>'}={}) {
  const dom = new JSDOM(html, {url:'https://site.example'+pathname+search,runScripts:'outside-only'});
  const {window:w}=dom; const calls=[]; const animations=[];
  Object.defineProperty(w,'innerWidth',{value:viewportWidth,writable:true,configurable:true});
  w.matchMedia=()=>({matches:false});
  w.HTMLElement.prototype.animate=function(keyframes,options){ const record={element:this,keyframes,options,cancelled:false}; animations.push(record); return {cancel(){record.cancelled=true;}}; };
  w.LEGEND_PUBLIC_CMS_CONTEXT={siteKey,apiBase:'',businessId: business?.id || '',pages};
  w.HTMLDialogElement.prototype.showModal = function() {}; w.HTMLDialogElement.prototype.close = function() { this.dispatchEvent(new w.Event('close')); };
  w.CSS={escape: v=>String(v).replaceAll('"','\\"')}; w.alert=()=>{}; w.confirm=()=>true;
  w.fetch=async(url,init={})=> { calls.push({url:String(url),...init}); const parsed=new URL(String(url)); const body=init.body?JSON.parse(init.body):null; if(parsed.pathname.endsWith('/manage/quality')) return {ok:!denied,status:denied?401:200,json:async()=>qualityPayload || {source:'saved_draft_server',revision:1,errorCount:0,warningCount:0,checks:[]}}; if(parsed.pathname.endsWith('/manage/media') && (!init.method || init.method==='GET')) return {ok:!denied,status:denied?401:200,json:async()=>mediaPayload || {assets:[]}}; if(parsed.pathname.endsWith('/manage/ai/propose')) return {ok:!denied,status:denied?401:200,json:async()=>aiPayload || {source:'ai_proposal_preview',baseRevision:'r1',summary:'No changes',operations:[],proposedDocument:doc,persisted:false,published:false}}; if(parsed.pathname.endsWith('/manage/signals/test')) return {ok:!denied,status:denied?401:200,json:async()=>signalTestPayload || {source:'website_signal_private_dry_run',dryRun:true,persisted:false,metaDispatched:false,stages:{mappingValidated:true,browserTriggerSupported:true,browserAnalyticsWouldBeAccepted:true,browserPixelWouldInvoke:false,serverOutcomeRequired:false},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false}}}; if(parsed.pathname.endsWith('/manage/signals/health')) return {ok:!denied,status:denied?401:200,json:async()=>signalHealthPayload || {source:'website_signal_existing_authorities',publishedVersionId:null,binding:{matchingConsent:'not_requested'},destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false},analytics:[],meta:[]}}; if(parsed.pathname.endsWith('/manage/collaboration') && (!init.method || init.method==='GET')) return {ok:!denied,status:denied?401:200,json:async()=>collaborationPayload || {source:'website_studio_collaboration',revision:'r1',role:{roleKey:'founder',label:'Founder',canPublish:true},collaborators:[{roleKey:'founder',displayName:'Founder',canPublish:true}],comments:[]}}; if(parsed.pathname.endsWith('/manage/collaboration/comments') || parsed.pathname.endsWith('/manage/collaboration/comments/status')) return {ok:!denied,status:denied?401:200,json:async()=>commentPayload || {source:'website_studio_collaboration',comment:{id:'comment-1',status:'open'}}}; return {ok:!denied,status:denied?401:200,json:async()=>({siteKey,business,revision:'r'+calls.length,document:body?.document || doc,ctaCatalog:{options:ctaCatalog},signalCatalog:signalCatalog || undefined})}; };
  w.eval(source);
  // JSDOM dispatches initial readiness itself; wait for the fetch continuation.
  await new Promise(resolve=>setTimeout(resolve,0));
  const click=selector=>w.document.querySelector(selector).dispatchEvent(new w.MouseEvent('click',{bubbles:true,cancelable:true}));
  const input=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const change=(selector,value)=> {const el=w.document.querySelector(selector);el.value=value;el.dispatchEvent(new w.Event('change',{bubbles:true}));};
  const editSelected=(value)=> {const el=w.document.querySelector('.legend-cms-selected');assert.ok(el);el.textContent=value;el.dispatchEvent(new w.Event('beforeinput',{bubbles:true,cancelable:true}));el.dispatchEvent(new w.Event('input',{bubbles:true}));};
  const save=async()=>{click('#legend-cms-save');input('#legend-cms-draft-name','Test variation');click('#legend-cms-draft-submit');await new Promise(resolve=>setTimeout(resolve,0));return JSON.parse(calls.at(-1).body).document;};
  return {w,calls,animations,click,input,change,editSelected,save,close:()=>w.close()};
}
async function metaSignalFixture() {
  const dom=new JSDOM('<!doctype html><html><body data-page-key="home"></body></html>',{url:'https://site.example/',runScripts:'outside-only'});
  const {window:w}=dom;
  const requests=[],pixels=[];
  w.fetch=async(url,init={})=>{
    requests.push({url:String(url),body:init.body?JSON.parse(init.body):null});
    return {ok:true,json:async()=>({accepted:true,metaServerStatus:'accepted_for_test'})};
  };
  w.fbq=(...args)=>pixels.push(args);
  w.eval(metaSignalSource);
  const session=w.metaSignalIntelligence.createLandingSession({
    enabled:true,
    sendBrowserEvents:true,
    sendServerEvents:true,
    persistEvents:true,
    endpoint:'https://site.example/analytics/meta-signal',
    siteKey:'legend',
    quoteType:'legend',
    pageKey:'home',
    effectivePageKey:'home',
    browserEventNames:['LeadFormStart'],
    browserSignalEventNames:['ViewContent','LeadFormStart','SubmitAttempt']
  });
  await new Promise(resolve=>setTimeout(resolve,0));
  requests.length=0;
  pixels.length=0;
  return {w,session,requests,pixels,close:()=>w.close()};
}


test('public runtime binds autonomous Meta contact tracking to the actual generated inquiry form id',()=>{
  assert.ok(source.includes("formId: inquiryForm?.id || inquiryForm?.dataset.formKey || ''"));
  assert.ok(metaSignalSource.includes('function wireContactInputs()'));
  assert.ok(metaSignalSource.includes("emitSignal('ContactInputStarted'"));
  assert.ok(metaSignalSource.includes("emitSignal('PhoneFieldCompleted'"));
  assert.ok(metaSignalSource.includes("emitSignal('RequiredContactFieldsCompleted'"));
  assert.ok(source.includes('Automatic form analytics + Meta'));
  assert.ok(source.includes('No mapping is required.'));
});

test('configured signal runtime suppresses Pixel for analytics-only and allows only Pixel-eligible Meta events',async()=>{
  const f=await metaSignalFixture();
  try{
    const analyticsId=await f.session.trackConfiguredEvent('SubmitAttempt',{
      deliveryMode:'analytics',
      onceKey:'website-binding:analytics-one',
      metadata:{websiteBindingId:'binding-analytics',elementId:'home.form',trigger:'submit_attempt',source:'website_signal_binding'}
    });
    assert.ok(analyticsId);
    assert.equal(f.pixels.length,0);
    assert.equal(f.requests.length,1);
    assert.equal(f.requests[0].body.eventName,'SubmitAttempt');
    assert.equal(f.requests[0].body.websiteBindingId,'binding-analytics');
    assert.equal(f.requests[0].body.metadata.browserDispatchStatus,'suppressed_by_mapping');
    assert.equal(f.requests[0].body.metadata.configuredWebsiteSignal,true);
    assert.equal(f.requests[0].body.metadata.configuredDeliveryMode,'analytics');

    f.requests.length=0;
    const metaId=await f.session.trackConfiguredEvent('LeadFormStart',{
      deliveryMode:'meta',
      onceKey:'website-binding:meta-one',
      metadata:{websiteBindingId:'binding-meta',elementId:'home.form',trigger:'form_started',source:'website_signal_binding'}
    });
    assert.ok(metaId);
    assert.equal(f.requests.length,1);
    assert.equal(f.requests[0].body.eventName,'LeadFormStart');
    assert.equal(f.pixels.length,1);
    assert.equal(f.pixels[0][0],'trackCustom');
    assert.equal(f.pixels[0][1],'LeadFormStart');

    const beforeRequests=f.requests.length,beforePixels=f.pixels.length;
    const blocked=await f.session.trackConfiguredEvent('Lead',{
      deliveryMode:'meta',
      onceKey:'website-binding:server-only',
      metadata:{websiteBindingId:'binding-server-only'}
    });
    assert.equal(blocked,null);
    assert.equal(f.requests.length,beforeRequests);
    assert.equal(f.pixels.length,beforePixels);
  } finally { f.close(); }
});

test('configured signal once-per-session key deduplicates repeated browser observations',async()=>{
  const f=await metaSignalFixture();
  try{
    const first=await f.session.trackConfiguredEvent('SubmitAttempt',{deliveryMode:'analytics',onceKey:'website-binding:once',metadata:{websiteBindingId:'binding-once'}});
    const second=await f.session.trackConfiguredEvent('SubmitAttempt',{deliveryMode:'analytics',onceKey:'website-binding:once',metadata:{websiteBindingId:'binding-once'}});
    assert.equal(first,second);
    assert.equal(f.requests.length,1);
  } finally { f.close(); }
});

test('published visual signal executor has no browser path for confirmed server outcomes',()=>{
  assert.ok(source.includes("const allowedTriggers = new Set(["));
  for(const trigger of ['viewed','click','form_started','submit_attempt','field_started','validation_failed','field_completed','scroll_threshold'])
    assert.ok(source.includes(`'${trigger}'`));
  assert.equal(source.includes("case 'submission_saved':"),false);
  assert.equal(source.includes("case 'booking_confirmed':"),false);
  assert.equal(source.includes("case 'payment_confirmed':"),false);
  assert.ok(source.includes("websiteBindingId: binding.id"));
  assert.ok(source.includes("source: 'website_signal_binding'"));
});

test('responsive V2 runtime inherits base style and switches breakpoint style and layout on resize',async()=>{
  const doc={
    breakpoints:[
      {key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true},
      {key:'tablet',label:'Tablet',minWidth:768,maxWidth:1199,isSystem:true},
      {key:'desktop',label:'Desktop',minWidth:1200,maxWidth:null,isSystem:true}
    ],
    elements:{
      'home.h1.template-title.1':{
        text:'Responsive heading',
        style:{widthPercent:80},
        breakpointStyles:{mobile:{widthPercent:100,fontScale:0.75},desktop:{widthPercent:60}}
      },
      'section:home.section.1':{
        layout:{mode:'grid',columns:3,gapPx:24},
        breakpointLayouts:{mobile:{mode:'stack',direction:'column',gapPx:12}}
      }
    }
  };
  const f=await domFixture({doc,search:'',viewportWidth:500});
  try {
    const heading=f.w.document.querySelector('main h1');
    const section=f.w.document.querySelector('main section');
    assert.equal(heading.textContent,'Responsive heading');
    assert.equal(heading.style.width,'100%');
    assert.equal(section.style.display,'flex');
    assert.equal(section.style.flexDirection,'column');
    assert.equal(section.style.gap,'12px');
    f.w.innerWidth=1400;
    f.w.dispatchEvent(new f.w.Event('resize'));
    assert.equal(heading.style.width,'60%');
    assert.equal(section.style.display,'grid');
    assert.equal(section.style.gridTemplateColumns,'repeat(3,minmax(0,1fr))');
    assert.equal(section.style.gap,'24px');
  } finally { f.close(); }
});



test('signal editor private test saves draft first but sends no production signal and shows dry-run result',async()=>{
  const catalog={events:[{name:'ViewContent',category:'page',metaEligible:true,requiresServerOutcome:false,triggers:['viewed']}],matchingFields:[],runtimeEnabled:true};
  const f=await domFixture({
    signalCatalog:catalog,
    signalTestPayload:{
      source:'website_signal_private_dry_run',dryRun:true,persisted:false,metaDispatched:false,
      destination:{ownerType:'business',browserPixelConfigured:false,serverCapiConfigured:false},
      stages:{mappingValidated:true,browserTriggerSupported:true,browserAnalyticsWouldBeAccepted:true,browserPixelWouldInvoke:false,serverOutcomeRequired:false}
    }
  });
  try{
    f.click('main h1');
    f.click('[data-open="signals"]');
    const add=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')].find(button=>button.textContent==='Add advanced custom mapping');
    assert.ok(add); add.click();
    const send=f.w.document.querySelector('#legend-cms-signal-controls select');
    send.value='analytics'; send.dispatchEvent(new f.w.Event('change',{bubbles:true}));
    const testButton=f.w.document.querySelector('[data-signal-test]');
    assert.ok(testButton);
    testButton.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    await new Promise(resolve=>setTimeout(resolve,0));

    const saveCall=f.calls.find(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage');
    const testCall=f.calls.find(call=>new URL(call.url).pathname.endsWith('/manage/signals/test'));
    assert.ok(saveCall);
    assert.ok(testCall);
    const request=JSON.parse(testCall.body);
    assert.equal(request.pagePath,'/');
    assert.equal(request.elementId,'home.h1.template-title.1');
    assert.equal(request.bindingId,testButton.dataset.signalTest);
    assert.equal(f.calls.some(call=>new URL(call.url).pathname==='/analytics/meta-signal'),false);
    const status=f.w.document.querySelector(`[data-signal-diagnostics="${testButton.dataset.signalTest}"]`).textContent;
    assert.match(status,/PRIVATE TEST/);
    assert.match(status,/no analytics or Meta event sent/);
    assert.match(status,/Analytics ingest: would accept/);
  } finally { f.close(); }
});

test('signal editor delivery health renders existing authoritative evidence without credential material',async()=>{
  const bindingId='11111111111111111111111111111111';
  const doc={pages:{'/':{elements:{'home.h1.template-title.1':{signals:[{id:bindingId,trigger:'viewed',eventName:'ViewContent',deliveryMode:'analytics',oncePerSession:true,matchingFields:[]}]}},sectionOrder:{},extras:[],navigation:{showInNavigation:true}}}};
  const catalog={events:[{name:'ViewContent',category:'page',metaEligible:true,requiresServerOutcome:false,triggers:['viewed']}],matchingFields:[],runtimeEnabled:true};
  const f=await domFixture({
    doc,signalCatalog:catalog,
    signalHealthPayload:{
      source:'website_signal_existing_authorities',
      publishedVersionId:'22222222-2222-2222-2222-222222222222',
      binding:{matchingConsent:'not_requested'},
      destination:{ownerType:'business',browserPixelConfigured:true,serverCapiConfigured:true,testEventCodeConfigured:false},
      analytics:[{eventType:'ViewContent',receivedUtc:'2026-09-24T20:00:00Z'}],
      meta:[{eventName:'ViewContent',metaBrowserSent:true,metaServerSent:false,dispatch:{status:'not_server_authority'}}]
    }
  });
  try{
    f.click('main h1'); f.click('[data-open="signals"]');
    const health=f.w.document.querySelector(`[data-signal-health="${bindingId}"]`);
    assert.ok(health); health.click();
    await new Promise(resolve=>setTimeout(resolve,0));
    const host=f.w.document.querySelector(`[data-signal-diagnostics="${bindingId}"]`);
    assert.match(host.textContent,/Destination owner: business/);
    assert.match(host.textContent,/Pixel: configured/);
    assert.match(host.textContent,/CAPI: configured/);
    assert.match(host.textContent,/Analytics accepted/);
    assert.match(host.textContent,/Meta signal/);
    assert.doesNotMatch(host.textContent,/token|ciphertext/i);
  } finally { f.close(); }
});

test('Collaboration reads canonical roles and posts private selection-anchored comments',async()=>{
  const collaborationPayload={
    source:'website_studio_collaboration',revision:'r1',
    role:{roleKey:'owner',label:'Owner',canComment:true,canResolveAll:true,canPublish:true},
    collaborators:[
      {roleKey:'owner',displayName:'Business Owner',canManageStorefront:true,canPublish:true},
      {roleKey:'member',displayName:'Website Editor',canManageStorefront:true,canPublish:false}
    ],
    comments:[]
  };
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},collaborationPayload});
  try{
    f.click('main h1');
    f.click('[data-open="collaboration"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-role').textContent,/Owner · can publish/);
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-roster').textContent,/Business Owner/);
    assert.match(f.w.document.querySelector('#legend-cms-collaboration-roster').textContent,/Website Editor/);
    f.input('#legend-cms-collaboration-body','Review this hero before publishing.');
    f.click('#legend-cms-collaboration-add');
    await new Promise(resolve=>setTimeout(resolve,0));
    const call=f.calls.find(call=>call.method==='POST' && new URL(call.url).pathname.endsWith('/manage/collaboration/comments'));
    assert.ok(call);
    const body=JSON.parse(call.body);
    assert.equal(body.ticket,'ticket');
    assert.equal(body.expectedRevision,'r1');
    assert.equal(body.pagePath,'/');
    assert.equal(body.elementId,'home.h1.template-title.1');
    assert.equal(body.body,'Review this hero before publishing.');
    assert.equal(body.parentCommentId,null);
    assert.equal(JSON.stringify(body).includes('document'),false);
  }finally{f.close();}
});

test('Collaboration source remains private management metadata and never joins published document serialization',()=>{
  assert.ok(source.includes('/manage/collaboration/comments'));
  assert.ok(source.includes('/manage/collaboration/comments/status'));
  assert.equal(source.includes('documentState.comments'),false);
  assert.equal(source.includes('pageState().comments'),false);
});

test('AI Assist generates a review-only proposal then uses normal save authority after explicit apply', async()=>{
  const proposed={
    version:2,
    breakpoints:[
      {key:'mobile',label:'Mobile',minWidth:0,maxWidth:767,isSystem:true},
      {key:'tablet',label:'Tablet',minWidth:768,maxWidth:1199,isSystem:true},
      {key:'desktop',label:'Desktop',minWidth:1200,maxWidth:null,isSystem:true}
    ],
    pages:{
      '/':{
        title:'Home',
        navigation:{showInNavigation:true,order:0,isDeleted:false},
        elements:{'home.h1.template-title.1':{text:'AI proposed headline',style:{}}},
        sectionOrder:{},
        extras:[]
      }
    },
    elements:{},sectionOrder:{},extras:[],reusableComponents:{},collections:{},theme:{}
  };
  const f=await domFixture({aiPayload:{
    source:'ai_proposal_preview',
    baseRevision:'r1',
    summary:'Improve the selected heading.',
    operations:[{kind:'set_text',text:'AI proposed headline'}],
    proposedDocument:proposed,
    persisted:false,
    published:false
  }});
  try{
    f.click('main h1');
    f.click('[data-open="ai"]');
    f.change('#legend-cms-ai-mode','create');
    f.input('#legend-cms-ai-prompt','Improve this headline.');
    f.click('#legend-cms-ai-generate');
    await new Promise(resolve=>setTimeout(resolve,0));

    const aiCall=f.calls.find(call=>new URL(call.url).pathname.endsWith('/manage/ai/propose'));
    assert.ok(aiCall);
    const aiRequest=JSON.parse(aiCall.body);
    assert.equal(aiRequest.ticket,'ticket');
    assert.equal(aiRequest.expectedRevision,'r1');
    assert.equal(aiRequest.pagePath,'/');
    assert.equal(aiRequest.selectedElementId,'home.h1.template-title.1');
    assert.equal(aiRequest.selectedText,'Template title');
    assert.equal(f.calls.some(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage'),false);
    assert.equal(f.w.document.querySelector('main h1').textContent,'Template title');
    assert.match(f.w.document.querySelector('#legend-cms-ai-proposal').textContent,/Improve the selected heading/);
    assert.equal(f.w.document.querySelector('#legend-cms-ai-apply').disabled,false);

    f.click('#legend-cms-ai-apply');
    assert.equal(f.w.document.querySelector('main h1').textContent,'AI proposed headline');
    assert.match(f.w.document.querySelector('#legend-cms-ai-status').textContent,/local draft/);

    const saved=await f.save();
    assert.equal(saved.pages['/'].elements['home.h1.template-title.1'].text,'AI proposed headline');
    assert.ok(f.calls.some(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage'));
  } finally { f.close(); }
});

test('AI Assist discards proposal without changing the current canvas', async()=>{
  const proposed={version:2,pages:{'/':{elements:{'home.h1.template-title.1':{text:'Should not apply'}},sectionOrder:{},extras:[],navigation:{showInNavigation:true}}},elements:{},sectionOrder:{},extras:[],theme:{}};
  const f=await domFixture({aiPayload:{source:'ai_proposal_preview',baseRevision:'r1',summary:'Discard me',operations:[{kind:'set_text',text:'Should not apply'}],proposedDocument:proposed,persisted:false,published:false}});
  try{
    f.click('main h1'); f.click('[data-open="ai"]'); f.change('#legend-cms-ai-mode','create'); f.input('#legend-cms-ai-prompt','Draft a change.');
    f.click('#legend-cms-ai-generate'); await new Promise(resolve=>setTimeout(resolve,0));
    f.click('#legend-cms-ai-discard');
    assert.equal(f.w.document.querySelector('main h1').textContent,'Template title');
    assert.equal(f.w.document.querySelector('#legend-cms-ai-apply').disabled,true);
    assert.equal(f.calls.some(call=>call.method==='POST' && new URL(call.url).pathname==='/api/website-content/manage'),false);
  } finally { f.close(); }
});

test('AI Assist source keeps provider proposal separate from normal draft persistence',()=>{
  assert.ok(source.includes("payload.source!=='ai_proposal_preview'"));
  assert.ok(source.includes("payload.persisted!==false"));
  assert.ok(source.includes("payload.published!==false"));
  assert.ok(source.includes("applyDocument(pendingAiProposal.proposedDocument)"));
  assert.equal(source.includes("/manage/ai/propose/publish"),false);
});

test('public declarative click motion plays once and is not duplicated by responsive refresh',async()=>{
  const id='11111111111111111111111111111111';
  const doc={elements:{'home.h1.template-title.1':{animations:[{id,trigger:'click',effect:'slide-up',durationMs:500,delayMs:25,distancePx:30,easing:'ease-out',once:true}]}}};
  const f=await domFixture({doc,search:''});
  try{
    const heading=f.w.document.querySelector('main h1');
    heading.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.animations.length,1);
    assert.equal(f.animations[0].options.duration,500);
    assert.equal(f.animations[0].options.delay,25);
    assert.equal(f.animations[0].options.easing,'ease-out');
    assert.match(String(f.animations[0].keyframes[0].transform),/translateY\(30px\)/);
    f.w.dispatchEvent(new f.w.Event('resize'));
    heading.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.animations.length,1);
  } finally { f.close(); }
});

test('Studio motion panel writes typed motion and preview does not create analytics requests',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-open="motion"]');
    f.click('#legend-cms-motion-controls > button');
    const row=f.w.document.querySelector('.legend-cms-motion-row');
    assert.ok(row);
    const selects=row.querySelectorAll('select');
    selects[0].value='hover'; selects[0].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    selects[1].value='scale'; selects[1].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    const numberInputs=row.querySelectorAll('input[type="number"]');
    numberInputs[0].value='650'; numberInputs[0].dispatchEvent(new f.w.Event('change',{bubbles:true}));
    f.click('.legend-cms-motion-row .legend-cms-row button');
    assert.equal(f.animations.length,1);
    assert.equal(f.calls.some(call=>new URL(call.url).pathname.includes('/tracking/')),false);
    const saved=await f.save();
    const override=Object.values(saved.pages['/'].elements).find(value=>Array.isArray(value.animations)&&value.animations.length);
    assert.ok(override);
    const motion=override.animations[0];
    assert.equal(motion.trigger,'hover');
    assert.equal(motion.effect,'scale');
    assert.equal(motion.durationMs,650);
    assert.match(motion.id,/^[a-f0-9]{32}$/);
  } finally { f.close(); }
});

test('motion runtime explicitly respects reduced-motion preference and never injects arbitrary scripts',()=>{
  assert.ok(source.includes("prefers-reduced-motion: reduce"));
  assert.ok(source.includes("typeof el.animate !== 'function' || prefersReducedMotion()"));
  assert.equal(source.includes('eval(binding'),false);
  assert.equal(source.includes('new Function(binding'),false);
});

test('canonical public stylesheet preserves authored spaces, tabs and line breaks',()=>{
  assert.match(publicCss,/\[data-cms-preserve-whitespace="true"\]\{white-space:pre-wrap;tab-size:4;overflow-wrap:anywhere\}/);
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
  assert.match(publicCss, /@media\(max-width:650px\)[\s\S]*?\.public-form-grid\{grid-template-columns:1fr;gap:14px\}/);
  assert.match(publicCss, /@container legend-public-preview \(max-width:650px\)[\s\S]*?\.public-form-grid\{grid-template-columns:1fr;gap:14px\}/);
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
  assert.ok(publicInquirySource.includes("form._trackSubmitAttempt?.(true, 0)"));
  assert.ok(publicInquirySource.includes("window.legendFormTracking?.markSubmitted?.("));
  assert.equal(publicInquirySource.includes("EventType: 'lead_form_submit_success'"),false);
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
    f.click('[data-open="signals"]');
    assert.match(f.w.document.querySelector('#legend-cms-signal-controls').textContent,/Automatic button analytics \+ Meta/);
    const advanced=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')]
      .find(node=>node.textContent==='Add advanced custom mapping');
    assert.ok(advanced); assert.equal(advanced.hidden,true);
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

test('new section inserts directly after the selected section and survives save reload ordering',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><h2>First</h2></section><section><h2>Middle</h2></section><section><h2>Last</h2></section></main></body></html>';
  const f=await domFixture({html}); let saved;
  try{
    const middle=f.w.document.querySelectorAll('main > section')[1];
    f.click('main > section:nth-of-type(2) h2');
    f.click('[data-add="section"]');
    const sections=[...f.w.document.querySelectorAll('main > section')];
    assert.equal(sections.length,4);
    const added=f.w.document.querySelector('.cms-extra-section');
    assert.ok(added);
    assert.equal(sections.indexOf(added),2);
    assert.equal(sections[1],middle);
    saved=await f.save();
    assert.equal(saved.pages['/'].sectionOrder[middle.dataset.cmsSection],1);
    assert.equal(saved.pages['/'].sectionOrder[added.dataset.cmsSection],2);
  }finally{f.close();}
  const loaded=await domFixture({html,doc:saved,search:''});
  try{
    const sections=[...loaded.w.document.querySelectorAll('main > section')];
    const added=loaded.w.document.querySelector('.cms-extra-section');
    assert.ok(added);
    assert.equal(sections.indexOf(added),2);
    assert.match(sections[1].textContent,/Middle/);
    assert.match(sections[3].textContent,/Last/);
  }finally{loaded.close();}
});

test('Layers lists and reorders only whole sections, never individual fields or actions',async()=>{
  const html='<!doctype html><html><body data-page-key="home"><main><section><h2>First</h2><form><input name="Email"><button>Send</button></form></section><section><h2>Second</h2><a href="/contact">Contact</a></section></main></body></html>';
  const f=await domFixture({html});
  try{
    f.click('[data-open="layers"]');
    let rows=[...f.w.document.querySelectorAll('.legend-cms-section-layer')];
    assert.equal(rows.length,2);
    assert.doesNotMatch(f.w.document.querySelector('#legend-cms-layers').textContent,/Email|Send|Contact/);
    assert.ok(rows.every(row=>row.draggable===true));
    const transfer={value:'',setData(_type,value){this.value=value;},getData(){return this.value;}};
    const start=new f.w.Event('dragstart',{bubbles:true,cancelable:true});
    Object.defineProperty(start,'dataTransfer',{value:transfer});
    rows[1].dispatchEvent(start);
    const over=new f.w.Event('dragover',{bubbles:true,cancelable:true});
    rows[0].dispatchEvent(over);
    const drop=new f.w.Event('drop',{bubbles:true,cancelable:true});
    Object.defineProperty(drop,'dataTransfer',{value:transfer});
    rows[0].dispatchEvent(drop);
    const sections=[...f.w.document.querySelectorAll('main > section')];
    assert.match(sections[0].textContent,/Second/);
    const saved=await f.save();
    assert.equal(saved.pages['/'].sectionOrder[sections[0].dataset.cmsSection],0);
    assert.equal(saved.pages['/'].sectionOrder[sections[1].dataset.cmsSection],1);
  }finally{f.close();}
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
test('breakpoint editor writes responsive style and layout without replacing base geometry',async()=>{
  const f=await domFixture();
  try {
    f.click('main h1');
    f.click('[data-open="layout"]');
    f.change('#legend-cms-breakpoint','mobile');
    f.input('#legend-cms-width','55');
    f.input('#legend-cms-layout-mode','stack');
    f.input('#legend-cms-layout-gap','16');
    const saved=await f.save();
    const override=Object.values(saved.pages['/'].elements).find(value=>value.breakpointStyles?.mobile?.widthPercent===55);
    assert.ok(override);
    assert.equal(override.style?.widthPercent,undefined);
    assert.equal(override.breakpointStyles.mobile.widthPercent,55);
    assert.equal(override.breakpointLayouts.mobile.mode,'stack');
    assert.equal(override.breakpointLayouts.mobile.gapPx,16);
  } finally { f.close(); }
});

test('custom breakpoint can be added used and removed without leaving hidden responsive state',async()=>{
  const f=await domFixture();
  try {
    f.click('main h1');
    f.click('[data-open="layout"]');
    f.input('#legend-cms-breakpoint-label','Large tablet');
    f.input('#legend-cms-breakpoint-key','large-tablet');
    f.input('#legend-cms-breakpoint-min','900');
    f.input('#legend-cms-breakpoint-max','1100');
    f.click('#legend-cms-breakpoint-add');
    assert.equal(f.w.document.querySelector('#legend-cms-breakpoint').value,'large-tablet');
    f.input('#legend-cms-width','66');
    f.click('#legend-cms-breakpoint-remove');
    const saved=await f.save();
    assert.equal(saved.breakpoints.some(value=>value.key==='large-tablet'),false);
    assert.equal(Object.values(saved.pages['/'].elements).some(value=>value.breakpointStyles?.['large-tablet']),false);
  } finally { f.close(); }
});
test('section deletion preserves child structure, reset restores, global theme remains separate',async()=>{
  const f=await domFixture();try {f.click('main h1');f.click('#legend-cms-container'); f.click('#legend-cms-remove');
    assert.equal(f.w.document.querySelector('main section').hidden,true);assert.ok(f.w.document.querySelector('main section h1'));
    const doc=await f.save();assert.equal(doc.pages['/'].elements['section:home.section.1'].hidden,true);
  }finally{f.close();}
});
test('inquiry form builder is autonomous and exposes no required manual mapping',async()=>{
  const catalog={events:[
    {name:'LeadFormStart',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['form_started']},
    {name:'ContactInputStarted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_started']},
    {name:'PhoneFieldCompleted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_completed']},
    {name:'RequiredContactFieldsCompleted',category:'lead',metaEligible:true,requiresServerOutcome:false,triggers:['field_completed']},
    {name:'SubmitAttempt',category:'submit',metaEligible:true,requiresServerOutcome:false,triggers:['submit_attempt']},
    {name:'Lead',category:'conversion',metaEligible:true,requiresServerOutcome:true,triggers:['submission_saved']}
  ],matchingFields:['email','phone','firstName','lastName'],runtimeEnabled:true};
  const f=await domFixture({signalCatalog:catalog});
  try{
    f.click('main h1');
    f.click('[data-add="form"]');
    const form=f.w.document.querySelector('form.cms-extra-form[data-website-inquiry]');
    assert.ok(form);
    assert.ok(form.id.startsWith('website_inquiry_'));
    assert.equal(form.dataset.formKey,'website_inquiry');
    assert.equal(form.dataset.preview,'');
    assert.equal(form.querySelector('fieldset').disabled,true);
    for(const name of ['FirstName','LastName','Phone','Email','Message'])
      assert.ok(form.querySelector(`[name="${name}"]`));
    assert.ok(form.querySelector('[name="consent"]'));
    f.click('[data-open="signals"]');
    const status=f.w.document.querySelector('.legend-cms-signal-presets');
    assert.ok(status);
    assert.match(status.textContent,/LeadFormStart/);
    assert.match(status.textContent,/ContactInputStarted/);
    assert.match(status.textContent,/PhoneFieldCompleted/);
    assert.match(status.textContent,/SubmitAttempt/);
    assert.match(status.textContent,/Lead/);
    const advanced=[...f.w.document.querySelectorAll('#legend-cms-signal-controls button')]
      .find(button=>button.textContent==='Add advanced custom mapping');
    assert.ok(advanced);
    assert.equal(advanced.hidden,true);
    const saved=await f.save();
    const extra=saved.pages['/'].extras.find(value=>value.type==='form');
    assert.ok(extra);
    assert.deepEqual(extra.signals,[]);
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
    assert.equal(f.w.document.querySelectorAll('[data-cms-gesture]').length,8);
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
test('selected content drag preserves free-form placement instead of forcing grid cells',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    heading.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:137,clientY:113,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:137,clientY:113,button:0}));
    const saved=await f.save();
    const style=Object.values(saved.pages['/'].elements)[0].style;
    assert.equal(style.offsetXPercent,6.167);
    assert.equal(style.offsetYPx,13);
  }finally{f.close();}
});

test('selected content can be pointer-dragged and is clamped inside its section frame',async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    const heading=f.w.document.querySelector('main h1');
    const section=heading.closest('[data-cms-section]');
    const preview=f.w.document.querySelector('.legend-cms-preview');
    assert.ok(section&&preview);
    heading.getBoundingClientRect=()=>({left:100,top:100,right:300,bottom:140,width:200,height:40});
    section.getBoundingClientRect=()=>({left:50,top:50,right:650,bottom:450,width:600,height:400});
    preview.getBoundingClientRect=()=>({left:0,top:0,right:1000,bottom:800,width:1000,height:800});
    heading.dispatchEvent(new f.w.MouseEvent('pointerdown',{bubbles:true,cancelable:true,clientX:100,clientY:100,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointermove',{bubbles:true,cancelable:true,clientX:1000,clientY:700,button:0}));
    f.w.dispatchEvent(new f.w.MouseEvent('pointerup',{bubbles:true,cancelable:true,clientX:1000,clientY:700,button:0}));
    const saved=await f.save();
    const style=Object.values(saved.pages['/'].elements)[0].style;
    assert.equal(style.offsetXPercent,58.333);
    assert.equal(style.offsetYPx,310);
  }finally{f.close();}
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
test('selected blocks use one sharp border with invisible directional resize zones and a quiet snap grid',async()=>{
 const f=await domFixture();try {
  f.click('main h1');
  const heading=f.w.document.querySelector('main h1');
  assert.equal(heading.draggable,false);
  const frame=f.w.document.querySelector('.legend-cms-selection-frame');
  assert.ok(frame);
  assert.equal(frame.querySelectorAll('.legend-cms-edge-handle').length,8);
  assert.equal(frame.querySelector('.legend-cms-move-handle'),null);
  assert.equal(frame.querySelector('.legend-cms-resize-handle'),null);
  assert.equal(source.includes('.legend-cms-move-handle{'),false);
  assert.equal(source.includes('.legend-cms-resize-handle{'),false);
  assert.match(source,/\.legend-cms-selection-frame\{[^}]*border:1px solid #d4ad45/);
  assert.match(source,/\.legend-cms-edge-right\{right:-6px\}/);
  assert.match(source,/\.legend-cms-corner-ne\{[^}]*cursor:nesw-resize/);
  assert.equal(/legend-cms-(?:resize|edge)[^\n]*border-radius:50%/.test(source),false);
  assert.match(source,/background-size:calc\(100% \/ 12\) 100%,100% 24px/);
  assert.match(source,/legend-cms-grid-overlay::before[^}]*opacity:0/);
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
      assert.equal(f.w.document.querySelectorAll('.legend-cms-tabs [data-open]').length, 15);
      assert.ok(f.w.document.querySelector('[data-open="signals"]'));
      assert.ok(f.w.document.querySelector('[data-open="quality"]'));
      assert.ok(f.w.document.querySelector('[data-open="collaboration"]'));
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


test('business Pages manager stores navigation metadata in the canonical document', async()=>{
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pages:[{path:'/',label:'Home'},{path:'/about',label:'About'},{path:'/services',label:'Services'}]
  });
  try {
    f.click('[data-open="page"]');
    assert.equal(f.w.document.querySelector('#legend-cms-page-business-tools').hidden,false);
    f.input('#legend-cms-page-nav-label','Start Here');
    f.input('#legend-cms-page-order','25');
    const visible=f.w.document.querySelector('#legend-cms-page-nav-visible');
    visible.checked=false;
    visible.dispatchEvent(new f.w.Event('input',{bubbles:true}));
    f.change('#legend-cms-page-parent','/about');
    const saved=await f.save();
    assert.equal(saved.pages['/'].navigation.label,'Start Here');
    assert.equal(saved.pages['/'].navigation.order,25);
    assert.equal(saved.pages['/'].navigation.showInNavigation,false);
    assert.equal(saved.pages['/'].navigation.parentPath,'/about');
  } finally { f.close(); }
});

test('business Pages manager creates a real custom page draft and saves before navigation', async()=>{
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}]
  });
  try {
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-nav-label','Team');
    f.input('#legend-cms-page-slug','/team');
    f.click('#legend-cms-page-create');
    await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    const saved=JSON.parse(saveCall.body).document;
    assert.equal(saved.pages['/team'].title,'Team');
    assert.equal(saved.pages['/team'].navigation.label,'Team');
    assert.equal(saved.pages['/team'].navigation.isDeleted,false);
    assert.ok(saved.pages['/team'].extras.some(extra=>extra.type==='section'));
    assert.ok(saved.pages['/team'].extras.some(extra=>extra.type==='text'&&extra.text==='Team'));
  } finally { f.close(); }
});

test('business Pages manager renames a template route with a tombstone and template identity', async()=>{
  const html='<!doctype html><html><head></head><body data-page-key="services"><main><section><h1>Services</h1></section></main></body></html>';
  const f=await domFixture({
    siteKey:'business',
    business:{id:'business-id',displayName:'Fixture business'},
    pathname:'/business-preview/services/',
    pages:[{path:'/',label:'Home'},{path:'/services',label:'Services'}],
    html
  });
  try {
    f.click('[data-open="page"]');
    f.input('#legend-cms-page-slug','/work');
    f.click('#legend-cms-page-rename');
    await new Promise(resolve=>setTimeout(resolve,0));
    const saveCall=f.calls.find(call=>call.method==='POST' && call.url.endsWith('/manage'));
    assert.ok(saveCall);
    const saved=JSON.parse(saveCall.body).document;
    assert.equal(saved.pages['/work'].templatePath,'/services');
    assert.equal(saved.pages['/services'].navigation.isDeleted,true);
    assert.equal(saved.pages['/services'].navigation.showInNavigation,false);
  } finally { f.close(); }
});

test('business Pages manager cannot delete or rename the home route', async()=>{
  const f=await domFixture({siteKey:'business',business:{id:'business-id',displayName:'Fixture business'},pages:[{path:'/',label:'Home'}]});
  try {
    f.click('[data-open="page"]');
    const remove=f.w.document.querySelector('#legend-cms-page-delete');
    assert.equal(remove.disabled,true);
    f.input('#legend-cms-page-slug','/new-home');
    f.click('#legend-cms-page-rename');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.calls.some(call=>call.method==='POST' && call.url.endsWith('/manage')),false);
  } finally { f.close(); }
});

test('LEGEND and Protect Pages views do not advertise arbitrary route creation before publication support exists', async()=>{
  for(const siteKey of ['legend','protect']){
    const f=await domFixture({siteKey,pages:[{path:'/',label:'Home'},{path:'/about',label:'About'}]});
    try{
      f.click('[data-open="page"]');
      assert.equal(f.w.document.querySelector('#legend-cms-page-business-tools').hidden,true);
      assert.equal(f.w.document.querySelector('#legend-cms-page-fixed-notice').hidden,false);
    } finally { f.close(); }
  }
});


test('media library reuses scoped image asset without persisting editor ticket', async()=>{
  const assetUrl='https://site.example/api/website-content/media/11111111-1111-1111-1111-111111111111';
  const f=await domFixture({mediaPayload:{assets:[{id:'11111111-1111-1111-1111-111111111111',name:'team-logo.png',url:assetUrl,contentType:'image/png',sizeBytes:2048,createdUtc:'2026-09-24T00:00:00Z'}]}});
  try{
    f.click('main img');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.match(f.w.document.querySelector('#legend-cms-media-status').textContent,/1 asset/);
    const preview=f.w.document.querySelector('.legend-cms-media-card img');
    assert.equal(new URL(preview.src).searchParams.get('ticket'),'ticket');
    f.click('.legend-cms-media-card button');
    const saved=await f.save();
    const imageOverride=Object.values(saved.pages['/'].elements).find(value=>value.imageDataUrl===assetUrl);
    assert.ok(imageOverride);
    assert.equal(JSON.stringify(saved).includes('ticket='),false);
    assert.ok(f.calls.some(call=>new URL(call.url).pathname.endsWith('/manage/media')));
  } finally { f.close(); }
});

test('media library inserts existing video into selected section through Extras', async()=>{
  const assetUrl='https://site.example/api/website-content/media/22222222-2222-2222-2222-222222222222';
  const f=await domFixture({mediaPayload:{assets:[{id:'22222222-2222-2222-2222-222222222222',name:'intro.mp4',url:assetUrl,contentType:'video/mp4',sizeBytes:8192,createdUtc:'2026-09-24T00:00:00Z'}]}});
  try{
    f.click('main h1');
    f.click('[data-open="media"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    f.click('.legend-cms-media-card button');
    const saved=await f.save();
    const video=saved.pages['/'].extras.find(extra=>extra.type==='video');
    assert.ok(video);
    assert.equal(video.videoUrl,assetUrl);
    assert.equal(JSON.stringify(saved).includes('ticket='),false);
  } finally { f.close(); }
});


test('added block can become one reusable definition and inserted instances remain references', async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-add="text"]');
    f.editSelected('Reusable promise');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Promise block');
    f.click('#legend-cms-component-save');
    const rows=f.w.document.querySelectorAll('.legend-cms-component-row');
    assert.equal(rows.length,1);
    rows[0].querySelectorAll('button')[0].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    const saved=await f.save();
    const definitions=Object.values(saved.reusableComponents);
    assert.equal(definitions.length,1);
    assert.equal(definitions[0].name,'Promise block');
    assert.equal(definitions[0].kind,'block');
    assert.equal(definitions[0].extras.length,1);
    assert.equal(definitions[0].extras[0].text,'Reusable promise');
    assert.deepEqual(definitions[0].extras[0].signals,[]);
    const instances=saved.pages['/'].extras.filter(extra=>extra.type==='reusable');
    assert.equal(instances.length,1);
    assert.equal(instances[0].syncSourceId,definitions[0].id);
    assert.equal(saved.pages['/'].extras.filter(extra=>extra.type==='text'&&extra.text==='Reusable promise').length,1);
  } finally { f.close(); }
});

test('updating reusable definition refreshes rendered instances and definition cannot delete while used', async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-add="text"]');
    f.editSelected('First component copy');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Shared text');
    f.click('#legend-cms-component-save');
    let row=f.w.document.querySelector('.legend-cms-component-row');
    row.querySelectorAll('button')[0].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.cms-reusable-instance').textContent,'First component copy');
    const original=[...f.w.document.querySelectorAll('.cms-extra-text')].find(node=>!node.closest('.cms-reusable-instance'));
    original.dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    f.editSelected('Updated component copy');
    f.click('[data-open="components"]');
    row=f.w.document.querySelector('.legend-cms-component-row');
    const buttons=row.querySelectorAll('button');
    buttons[1].dispatchEvent(new f.w.MouseEvent('click',{bubbles:true,cancelable:true}));
    assert.equal(f.w.document.querySelector('.cms-reusable-instance').textContent,'Updated component copy');
    row=f.w.document.querySelector('.legend-cms-component-row');
    assert.equal(row.querySelectorAll('button')[2].disabled,true);
    const saved=await f.save();
    assert.equal(Object.values(saved.reusableComponents)[0].extras[0].text,'Updated component copy');
    assert.equal(saved.pages['/'].extras.filter(extra=>extra.type==='reusable').length,1);
  } finally { f.close(); }
});

test('template content cannot be serialized into reusable component storage', async()=>{
  const f=await domFixture();
  try{
    f.click('main h1');
    f.click('[data-open="components"]');
    f.input('#legend-cms-component-name','Should not copy template');
    f.click('#legend-cms-component-save');
    assert.match(f.w.document.querySelector('#legend-cms-component-status').textContent,/Select an added block or added section/);
    const saved=await f.save();
    assert.deepEqual(saved.reusableComponents,{});
  } finally { f.close(); }
});

test('missing reusable definition renders editor warning without copied fallback content', async()=>{
  const doc={pages:{'/':{elements:{},sectionOrder:{},extras:[{id:'missing-instance',type:'reusable',sectionId:'home.section.1',syncSourceId:'missing-component',style:{}}]}}};
  const f=await domFixture({doc});
  try{
    assert.match(f.w.document.querySelector('.cms-reusable-instance').textContent,/Reusable component is unavailable/);
    const saved=await f.save();
    const instance=saved.pages['/'].extras.find(extra=>extra.id==='missing-instance');
    assert.equal(instance.syncSourceId,'missing-component');
    assert.equal(saved.pages['/'].extras.length,1);
  } finally { f.close(); }
});

test('quality inspector keeps saved-server checks separate from rendered-canvas checks', async () => {
  const html='<!doctype html><html><head></head><body data-page-key="home"><main><section><h1 id="duplicate">Title</h1><p id="duplicate">Copy</p><img src="https://images.example/a.png"><a href="#">Broken</a><input name="email"></section></main></body></html>';
  const qualityPayload={source:'saved_draft_server',revision:7,errorCount:1,warningCount:1,checks:[
    {code:'dynamic_collection_missing',severity:'error',message:'Saved draft dynamic collection is unavailable.'},
    {code:'page_title_missing',severity:'warning',message:'Saved draft page title is missing.'}
  ]};
  const f=await domFixture({html,qualityPayload});
  try {
    f.click('[data-open="quality"]');
    await new Promise(resolve=>setTimeout(resolve,0));
    const savedMeta=f.w.document.querySelector('#legend-cms-quality-saved-meta').textContent;
    const liveMeta=f.w.document.querySelector('#legend-cms-quality-live-meta').textContent;
    const savedText=f.w.document.querySelector('#legend-cms-quality-saved').textContent;
    const liveText=f.w.document.querySelector('#legend-cms-quality-live').textContent;
    assert.match(savedMeta,/Saved draft checks \(server\) · revision 7 · 1 errors · 1 warnings/);
    assert.match(liveMeta,/Live page checks \(rendered canvas\)/);
    assert.match(savedText,/Saved draft dynamic collection is unavailable/);
    assert.doesNotMatch(savedText,/Duplicate rendered id/);
    assert.match(liveText,/Duplicate rendered id "duplicate"/);
    assert.match(liveText,/missing alternative text/);
    assert.match(liveText,/no working destination/);
    assert.match(liveText,/no accessible label/);
    assert.ok(f.calls.some(call=>new URL(call.url).pathname.endsWith('/manage/quality')));
  } finally { f.close(); }
});

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
