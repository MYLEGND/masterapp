import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';
const source = readFileSync(new URL('../../SHARED/WebsitePlatform/tracking.js', import.meta.url), 'utf8');
const cmsSource = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const layout = readFileSync(new URL('../../Protect-Website/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
function fixture() {
  const dom = new JSDOM('<body data-page-key="home"><form data-form-key="inquiry"><input name="FirstName"></form></body>', {
    url: 'https://protect.example.test/', runScripts: 'outside-only', pretendToBeVisual: true
  });
  const { window } = dom;
  const events = [];
  window.LEGEND_ANALYTICS_CONFIG = {allowedBrowserEvents:['page_view','form_start','cta_click'],criticalBrowserEvents:[]};
  window.fetch = async (url, options) => { events.push(JSON.parse(options.body)); return {ok:true,status:200}; };
  return {dom, window, events};
}
test('failed tracker installation rolls back listeners and retries with one page view and form start', async () => {
  const f = fixture();
  try {
    const form = f.window.document.querySelector('form');
    const add = form.addEventListener.bind(form);
    form.addEventListener = (type, ...args) => { if(type === 'submit') throw new Error('startup fixture failure'); return add(type,...args); };
    const originalFetch = f.window.fetch;
    assert.throws(() => f.window.eval(source), /startup fixture failure/);
    assert.equal(f.window.__legendTrackingInitialized, false);
    assert.equal(f.window.fetch, originalFetch);
    assert.equal(form._legendTrackingBound, undefined);
    assert.equal(form._trackSubmitAttempt, undefined);
    assert.equal(f.events.length, 0);
    form.addEventListener = add;
    f.window.eval(source);
    assert.equal(f.window.__legendTrackingInitialized, true);
    form.querySelector('input').dispatchEvent(new f.window.FocusEvent('focusin',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,10));
    assert.equal(f.events.filter(event=>event.EventType==='page_view').length,1);
    assert.equal(f.events.filter(event=>event.EventType==='form_start').length,1);
    f.window.eval(source);
    assert.equal(f.events.filter(event=>event.EventType==='page_view').length,1);
  } finally {f.dom.window.close();}
});
test('first party startup is independent of throwing optional provider globals',()=>{
  const f=fixture();
  try {
    Object.defineProperty(f.window,'fbq',{get(){throw new Error('optional provider unavailable');}});
    Object.defineProperty(f.window,'LegendOpenAiMeasurement',{get(){throw new Error('optional provider unavailable');}});
    f.window.eval(source);
    assert.equal(f.window.__legendTrackingInitialized,true);
    assert.equal(f.events.filter(event=>event.EventType==='page_view').length,1);
  } finally {f.dom.window.close();}
});
test('CMS requires successful tracker execution before optional providers and Protect includes first party first',()=>{
  const start=cmsSource.indexOf('async function startPublicRuntime()');
  const runtime=cmsSource.slice(start,cmsSource.indexOf('async function loadPublic()',start));
  assert.ok(runtime.indexOf('window.__legendTrackingInitialized !== true') < runtime.indexOf('initializeMetaPixel(meta.pixelId)'));
  assert.ok(runtime.indexOf('window.__legendTrackingInitialized !== true') < runtime.indexOf('publicRuntimeStarted = true'));
  assert.match(runtime,/schedulePublicRuntimeRetry\(\)/);
  assert.ok(layout.indexOf('src="~/js/tracking.js"') < layout.indexOf('src="~/js/openai-measurement.js"'));
});

test('CMS retries a failed tracker execution before starting providers, then ignores repeated startup',async()=>{
  const f=fixture();
  try {
    const {window}=f;
    const scripts=[], providers=[], errors=[];
    let succeed=false,retries=0;
    const start=cmsSource.indexOf('  async function startPublicRuntime()');
    const end=cmsSource.indexOf('  async function loadPublic()',start);
    window.fetch=async()=>({ok:true,json:async()=>({analytics:{endpoint:'/api/tracking/ingest',allowedBrowserEvents:['page_view']},meta:{pixelId:'test-pixel'}})});
    window.runtimeTest={
      async load(src){
        scripts.push(src);
        const element=window.document.createElement('script');element.src=src;window.document.head.appendChild(element);
        if(succeed){window.__legendTrackingInitialized=true;window.LegendAnalytics={track(){}};}
      },
      retry(){retries++;},provider(id){providers.push(id);},error(...args){errors.push(args);}
    };
    window.eval(`let publicRuntimeStarted=false, publicRuntimeStarting=false, publicRuntimeRetryCount=0, publicRuntimeRetryTimer=null;
      const editorMode=false, renderInput=null, SITE_KEY='legend', API_BASE=location.origin, pageKey='home';
      const context={trackingAsset:'/legend-public-tracking.js'};let ctaCatalog=[];
      const applyRuntimeActionContracts=()=>{},loadRuntimeScript=runtimeTest.load,schedulePublicRuntimeRetry=runtimeTest.retry,initializeMetaPixel=runtimeTest.provider;
      ${cmsSource.slice(start,end)}
      window.startRuntimeTest=startPublicRuntime;`);
    window.console.error=window.runtimeTest.error;
    await window.startRuntimeTest();
    assert.equal(retries,1);
    assert.equal(window.document.querySelectorAll('script').length,0);
    assert.deepEqual(providers,[]);
    succeed=true;
    await window.startRuntimeTest();
    assert.deepEqual(providers,['test-pixel']);
    assert.equal(scripts.length,2);
    await window.startRuntimeTest();
    assert.equal(scripts.length,2);
    assert.equal(providers.length,1);
    assert.equal(errors.length,1);
  } finally {f.dom.window.close();}
});

test('late provider projections share one accepted page event and unique signals use the sole ingest', async()=>{
  const f=fixture();
  try {
    const w=f.window, pixels=[], openai=[];
    w.LEGEND_ANALYTICS_CONFIG.allowedBrowserEvents.push('PhoneFieldCompleted','meta_browser_event_success');
    w.LEGEND_ANALYTICS_CONFIG.signalAliases={page_view:'ViewContent',form_start:'LeadFormStart',PhoneFieldCompleted:'PhoneFieldCompleted'};
    w.eval(source);
    await new Promise(resolve=>setTimeout(resolve,0));
    w.fbq=(...args)=>pixels.push(args);
    w.oaiq=(...args)=>openai.push(args);
    w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/meta-signal-intelligence.js',import.meta.url),'utf8'));
    const session=w.metaSignalIntelligence.createLandingSession({enabled:true,sendBrowserEvents:true,sendServerEvents:true,persistEvents:true,pixelId:'meta-test',siteKey:'protect',pageKey:'home',browserEventNames:['ViewContent','LeadFormStart'],browserSignalEventNames:['ViewContent','LeadFormStart','PhoneFieldCompleted']});
    w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/openai-measurement.js',import.meta.url),'utf8'));
    void w.LegendOpenAiMeasurement.configure({pixelId:'openai-test'});
    const page=f.events.find(e=>e.EventType==='page_view');
    const meta=pixels.find(args=>args[2]==='ViewContent');
    assert.ok(meta);
    assert.equal(meta.at(-1).eventID,page.ClientEventId);
    assert.equal(openai.find(args=>args[0]==='measureSingle').at(-1).event_id,page.ClientEventId);
    assert.equal(f.events.filter(e=>e.EventType==='ViewContent').length,0);
    await session.trackConfiguredEvent('PhoneFieldCompleted',{deliveryMode:'analytics',onceKey:'phone',metadata:{websiteBindingId:'phone'}});
    const unique=f.events.find(e=>e.EventType==='PhoneFieldCompleted');
    assert.ok(unique);
    assert.equal(unique.ClientEventId.replaceAll('-',''),unique.MetaSignal.eventId);
    assert.match(unique.ClientEventId,/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/);
    w.document.querySelector('input').dispatchEvent(new w.FocusEvent('focusin',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.events.filter(e=>e.EventType==='form_start').length,1);
    assert.equal(f.events.filter(e=>e.EventType==='LeadFormStart').length,0);
    let replayed=0;
    w.LegendAnalytics.subscribe('test',()=>{replayed++;});
    const prior=replayed;
    w.LegendAnalytics.subscribe('test',()=>{replayed++;});
    assert.equal(replayed,prior);
  } finally {f.dom.window.close();}
});

test('managed form bindings enrich the one canonical envelope and preserve delivery policy',async()=>{
  const f=fixture();
  try {
    const w=f.window;w.eval(source);
    const form=w.document.querySelector('form');
    w.LegendAnalytics.registerBinding(form,{id:'visual-form',eventName:'LeadFormStart',trigger:'form_started',deliveryMode:'analytics',oncePerSession:true},'home.form');
    form.querySelector('input').dispatchEvent(new w.FocusEvent('focusin',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    const starts=f.events.filter(e=>e.EventType==='form_start');
    assert.equal(starts.length,1);
    assert.equal(starts[0].WebsiteBindingId,'visual-form');
    const binding=JSON.parse(starts[0].MetadataJson).configuredSignalBindings[0];
    assert.equal(binding.deliveryMode,'analytics');
    assert.equal(binding.eventName,'LeadFormStart');
    assert.equal(binding.elementId,'home.form');
    assert.equal(f.events.filter(e=>e.EventType==='LeadFormStart').length,0);
  } finally {f.dom.window.close();}
});
