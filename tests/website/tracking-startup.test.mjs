import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';
const source = readFileSync(new URL('../../SHARED/WebsitePlatform/tracking.js', import.meta.url), 'utf8');
const cmsSource = readFileSync(new URL('../../SHARED/WebsitePlatform/legend-public-cms.js', import.meta.url), 'utf8');
const layout = readFileSync(new URL('../../Protect-Website/Views/Shared/_Layout.cshtml', import.meta.url), 'utf8');
const metaSource = readFileSync(new URL('../../SHARED/WebsitePlatform/meta-signal-intelligence.js', import.meta.url), 'utf8');
const openAiSource = readFileSync(new URL('../../SHARED/WebsitePlatform/openai-measurement.js', import.meta.url), 'utf8');
test('linked storefront CMS preserves the owning tracker and starts no second provider runtime', async () => {
  const f = fixture();
  try {
    const w = f.window;
    w.LEGEND_ANALYTICS_CONFIG.runtimeOwner = 'commerce';
    w.eval(source);
    const config = w.LEGEND_ANALYTICS_CONFIG, tracker = w.LegendAnalytics;
    const calls = [];
    w.fetch = async () => ({ok:true,json:async()=>({analytics:{endpoint:'/api/tracking/ingest',allowedBrowserEvents:['page_view']},meta:{enabled:true,pixelId:'other-meta'},openai:{enabled:true,pixelId:'other-openai'}})});
    w.runtimeTest = {load:async x=>calls.push(x),provider:x=>calls.push(x),retry:()=>calls.push('retry'),bindings:()=>calls.push('bindings')};
    const start = cmsSource.indexOf('  async function startPublicRuntime()');
    const end = cmsSource.indexOf('  async function loadPublic()', start);
    w.eval(`let publicRuntimeStarted=false,publicRuntimeStarting=false,publicRuntimeRetryCount=0,publicRuntimeRetryTimer=null;
      const editorMode=false,renderInput=null,SITE_KEY='business',API_BASE=location.origin,pageKey='store';
      const context={trackingAsset:'/legend-public-tracking.js',metaSignalAsset:'/meta.js',openAiMeasurementAsset:'/openai.js'};let ctaCatalog=[];
      const applyRuntimeActionContracts=()=>{},installPublishedSignalBindings=runtimeTest.bindings,loadRuntimeScript=runtimeTest.load,schedulePublicRuntimeRetry=runtimeTest.retry,initializeMetaPixel=runtimeTest.provider;
      ${cmsSource.slice(start,end)}
      window.startRuntimeTest=startPublicRuntime;`);
    await w.startRuntimeTest(); await w.startRuntimeTest();
    assert.equal(w.LEGEND_ANALYTICS_CONFIG,config);
    assert.equal(w.LegendAnalytics,tracker);
    assert.deepEqual(calls,['bindings']);
    assert.equal(f.events.filter(e=>e.EventType==='page_view').length,1);
  } finally { f.dom.window.close(); }
});
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

test('Website Studio is a zero-production-signal environment across analytics and provider runtimes',async()=>{
  const dom=new JSDOM('<body data-page-key="home"><form data-form-key="inquiry"><input name="FirstName"><button type="submit">Send</button></form></body>',{
    url:'https://protect.example.test/a/agent/contact?legendEdit=ticket',
    runScripts:'outside-only',
    pretendToBeVisual:true
  });
  const w=dom.window;
  const requests=[];
  const pixels=[];
  const openai=[];
  try{
    w.LEGEND_ANALYTICS_CONFIG={allowedBrowserEvents:['page_view','form_start','cta_click'],criticalBrowserEvents:[]};
    w.fetch=async(...args)=>{requests.push(args);return {ok:true,status:200};};
    w.fbq=(...args)=>pixels.push(args);
    w.oaiq=(...args)=>openai.push(args);

    w.eval(source);
    w.eval(metaSource);
    w.eval(openAiSource);

    assert.equal(w.__legendTrackingInitialized,undefined);
    assert.equal(w.__legendTrackingSuppressedForWebsiteStudio,true);
    assert.equal(w.__legendMetaSignalSuppressedForWebsiteStudio,true);
    assert.equal(w.__legendOpenAiMeasurementSuppressedForWebsiteStudio,true);
    assert.equal(w.LegendAnalytics,undefined);
    assert.equal(w.metaSignalIntelligence,undefined);
    assert.equal(w.LegendOpenAiMeasurement,undefined);
    assert.equal(requests.length,0);
    assert.equal(pixels.length,0);
    assert.equal(openai.length,0);
  }finally{dom.window.close();}
});

test('Protect layout never bootstraps production measurement or lead runtime in Website Studio mode',()=>{
  assert.match(layout,/websiteStudioMode\s*=\s*[\s\S]*ContainsKey\("legendEdit"\)[\s\S]*ContainsKey\("legendMaterialize"\)/);
  assert.match(layout,/window\.LEGEND_WEBSITE_STUDIO_MODE\s*=/);
  assert.match(layout,/@if \(!websiteStudioMode\)[\s\S]*src="~\/js\/tracking\.js"/);
  assert.doesNotMatch(layout,/lead-modal\.js/);
  assert.doesNotMatch(layout,/id="leadModal"|id="leadForm"/);
  assert.match(layout,/type="module" src="~\/js\/public-inquiry-form\.mjs"/);
  assert.match(layout,/@if \(!websiteStudioMode\)[\s\S]*_PageHealth\.cshtml/);
});

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
      const applyRuntimeActionContracts=()=>{},installPublishedSignalBindings=()=>{},loadRuntimeScript=runtimeTest.load,schedulePublicRuntimeRetry=runtimeTest.retry,initializeMetaPixel=runtimeTest.provider;
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
    const session=w.metaSignalIntelligence.createLandingSession({enabled:true,sendBrowserEvents:true,sendServerEvents:true,persistEvents:true,pixelId:'meta-test',siteKey:'protect',pageKey:'home',browserEventNames:['ViewContent'],browserSignalEventNames:['ViewContent','LeadFormStart','PhoneFieldCompleted']});
    w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/openai-measurement.js',import.meta.url),'utf8'));
    void w.LegendOpenAiMeasurement.configure({pixelId:'openai-test'});
    const page=f.events.find(e=>e.EventType==='page_view');
    const meta=pixels.find(args=>args[2]==='ViewContent');
    assert.ok(meta);
    assert.equal(meta.at(-1).eventID,page.ClientEventId);
    assert.equal(openai.find(args=>args[0]==='measureSingle').at(-1).event_id,page.ClientEventId);
    assert.equal(f.events.filter(e=>e.EventType==='ViewContent').length,0);
    await w.LegendAnalytics.trackBinding({id:'phone',eventName:'PhoneFieldCompleted',deliveryMode:'analytics',oncePerSession:true},{elementId:'phone'});
    const unique=f.events.find(e=>e.EventType==='PhoneFieldCompleted');
    assert.ok(unique);
    assert.equal(unique.WebsiteBindingId,'phone');
    assert.match(unique.ClientEventId,/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/);
    w.document.querySelector('input').dispatchEvent(new w.FocusEvent('focusin',{bubbles:true}));
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(f.events.filter(e=>e.EventType==='form_start').length,1);
    assert.equal(f.events.filter(e=>e.EventType==='LeadFormStart').length,0);
    assert.equal(pixels.filter(args=>args[2]==='LeadFormStart').length,0);
    assert.equal(openai.filter(args=>args[0]==='measureSingle').length,1);
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

test('custom wording cannot change managed action identity and contact starts automatically once',async()=>{
 const f=fixture();
 try {
  const w=f.window;
  w.LEGEND_ANALYTICS_CONFIG.allowedBrowserEvents.push('form_field_focus');
  const button=w.document.createElement('a');button.href='#';button.dataset.websiteActionKey='business_schedule';
  button.dataset.websiteBindingId='binding-1';button.dataset.cmsId='home.button.node.5';button.dataset.websiteAnalyticsEvent='cta_click';
  button.textContent='Book consultation';w.document.body.appendChild(button);
  w.eval(source);
  button.click();button.textContent='Purchase confirmed';button.click();
  const field=w.document.querySelector('input');
  field.dispatchEvent(new w.FocusEvent('focusin',{bubbles:true}));
  field.dispatchEvent(new w.FocusEvent('focusin',{bubbles:true}));
  await new Promise(resolve=>setTimeout(resolve,0));
  const clicks=f.events.filter(e=>e.EventType==='cta_click');
  assert.equal(clicks.length,2);
  for(const click of clicks){assert.equal(click.ActionKey,'business_schedule');assert.equal(click.WebsiteBindingId,'binding-1');assert.equal(click.ElementKey,'home.button.node.5');}
  assert.equal(clicks[1].ButtonLabel,'Purchase confirmed');
  assert.equal(f.events.filter(e=>e.EventType==='Purchase').length,0);
  assert.equal(f.events.filter(e=>e.EventType==='form_field_focus').length,1);
 }finally{f.dom.window.close();}
});

test('editor binding uses canonical tracker without any provider and blocks server-only names',async()=>{
 const f=fixture();try{
  const w=f.window;w.LEGEND_ANALYTICS_CONFIG.allowedBrowserEvents.push('form_field_focus');w.eval(source);
  const binding={id:'contact-input',eventName:'form_field_focus',actionKey:'contact_input_started',deliveryMode:'destinations',oncePerSession:true};
  assert.equal(await w.LegendAnalytics.trackBinding(binding,{elementId:'field-1'}),true);
  assert.equal(await w.LegendAnalytics.trackBinding(binding,{elementId:'field-1'}),true);
  const events=f.events.filter(e=>e.WebsiteBindingId==='contact-input');assert.equal(events.length,1);
  assert.equal(events[0].ActionKey,'contact_input_started');
  assert.equal(JSON.parse(events[0].MetadataJson).configuredDeliveryMode,'destinations');
  assert.equal(await w.LegendAnalytics.trackBinding({...binding,id:'forged',eventName:'Purchase'}),false);
  assert.equal(f.events.some(e=>e.EventType==='Purchase'),false);
 }finally{f.dom.window.close();}
});

test('managed form and cart presets delegate native behavior without claiming confirmed outcomes',async()=>{
 const f=fixture();try{
  const w=f.window;let cartCommands=0;
  const product=w.document.createElement('button');product.id='spAddToCart';product.dataset.productId='sku';
  product.addEventListener('click',()=>cartCommands++);w.document.body.appendChild(product);
  const action=(key,runtime)=>{const a=w.document.createElement('a');a.href='#';a.dataset.websiteActionKey=key;a.dataset.websiteRuntimeAction=runtime;a.dataset.websiteAnalyticsEvent='cta_click';w.document.body.appendChild(a);return a;};
  const focus=action('form_start','focus_form'),cart=action('commerce_add_to_cart','add_current_product');
  w.eval(source);focus.click();cart.click();
  await new Promise(resolve=>setTimeout(resolve,0));
  assert.equal(w.document.activeElement,w.document.querySelector('input'));
  assert.equal(cartCommands,1);
  assert.equal(f.events.filter(e=>e.EventType==='form_start').length,1);
  assert.equal(f.events.filter(e=>e.ActionKey==='commerce_add_to_cart').length,1);
  assert.equal(f.events.some(e=>['AddToCart','InitiateCheckout','Purchase','Lead'].includes(e.EventType)),false);
 }finally{f.dom.window.close();}
});

test('explicit template action retains legacy click instrumentation without a second writer',async()=>{
 const f=fixture();try{
  const w=f.window;
  w.document.body.dataset.pageCategory='quote';
  w.LEGEND_ANALYTICS_CONFIG.allowedBrowserEvents.push('quote_click','quote_cta_click');
  const link=w.document.createElement('a');link.href='#';link.dataset.cta='nav_quote';link.dataset.websiteActionKey='protect_quote';link.dataset.websiteAnalyticsEvent='quote_click';w.document.body.appendChild(link);
  w.eval(source);link.click();await new Promise(resolve=>setTimeout(resolve,0));
  const events=f.events.filter(e=>e.EventType==='quote_click');assert.equal(events.length,1);
  assert.equal(events[0].ActionKey,'protect_quote');
  assert.equal(f.events.filter(e=>e.EventType==='quote_cta_click').length,0);
 }finally{f.dom.window.close();}
});


test('canonical measurement consent blocks provider cookies and browser projections while first-party analytics continues', async()=>{
  const f=fixture();
  try {
    const w=f.window, pixels=[], openai=[];
    Object.defineProperty(w.navigator,'globalPrivacyControl',{value:true,configurable:true});
    w.document.cookie='__obref=browser-ref; Path=/';
    w.document.cookie='_fbp=fb-browser; Path=/';
    w.document.cookie='_fbc=fb-click; Path=/';
    w.eval(source);
    await new Promise(resolve=>setTimeout(resolve,0));
    assert.equal(w.LegendAnalytics.measurementConsent.isAllowed(),false);
    assert.equal(f.events.filter(e=>e.EventType==='page_view').length,1);
    assert.equal(w.document.cookie.includes('__obref='),false);
    assert.equal(w.document.cookie.includes('_fbp='),false);
    assert.equal(w.document.cookie.includes('_fbc='),false);

    w.fbq=(...args)=>pixels.push(args);
    w.oaiq=(...args)=>openai.push(args);
    w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/meta-signal-intelligence.js',import.meta.url),'utf8'));
    w.metaSignalIntelligence.createLandingSession({enabled:true,sendBrowserEvents:true,sendServerEvents:true,persistEvents:true,pixelId:'meta-test',siteKey:'protect',pageKey:'home',browserEventNames:['ViewContent'],browserSignalEventNames:['ViewContent']});
    w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/openai-measurement.js',import.meta.url),'utf8'));
    await w.LegendOpenAiMeasurement.configure({pixelId:'openai-test'});
    assert.equal(pixels.some(args=>args[0]==='trackSingle'),false);
    assert.equal(openai.some(args=>args[0]==='measureSingle'),false);
    assert.equal(openai.some(args=>args[0]==='init'),false);
  } finally { f.dom.window.close(); }
});

test('canonical measurement consent choice is shared with every provider adapter', async()=>{
  const f=fixture();
  try {
    const w=f.window;
    w.eval(source);
    assert.equal(w.LegendAnalytics.measurementConsent.isAllowed(),true);
    const denied=w.LegendAnalytics.measurementConsent.set(false,'test');
    assert.equal(denied.allowed,false);
    assert.match(w.document.cookie,/legend_measurement_consent=denied/);
    const granted=w.LegendAnalytics.measurementConsent.set(true,'test');
    assert.equal(granted.allowed,true);
    assert.match(w.document.cookie,/legend_measurement_consent=granted/);
  } finally { f.dom.window.close(); }
});
