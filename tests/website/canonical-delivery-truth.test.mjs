import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';
const source = readFileSync(new URL('../../SHARED/WebsitePlatform/tracking.js', import.meta.url), 'utf8');
const key = 'legend_tracking_event_queue_v1:audit';
const pause = () => new Promise(resolve => setTimeout(resolve, 0));
const receipt = eligible => ({ok:true,status:200,json:async()=>({marketingEligibility:{eligible,score:eligible?95:0}})});
function fixture() {
  const dom = new JSDOM('<body data-page-key="home"></body>', {url:'https://example.test/',runScripts:'outside-only',pretendToBeVisual:true});
  dom.window.LEGEND_ANALYTICS_CONFIG={attributionScope:'audit',allowedBrowserEvents:['page_view','cta_click'],criticalBrowserEvents:[]};
  return dom;
}
function item(id) { return {body:{ClientEventId:id,EventType:'cta_click',SessionId:'session',VisitorId:'visitor'},retryCount:0}; }

test('successful sendBeacon retains the exact event until acknowledged fetch replay',async()=>{
  const dom=fixture(), w=dom.window;
  try {
    w.fetch=async()=>receipt(false); w.eval(source); await pause();
    const original=item('10000000-0000-4000-8000-000000000001');
    w.localStorage.setItem(key,JSON.stringify([original]));
    let beacons=0; w.navigator.sendBeacon=()=>{beacons++;return true;};
    w.dispatchEvent(new w.Event('pagehide')); await pause();
    assert.ok(beacons>0);
    const retained=JSON.parse(w.localStorage.getItem(key));
    assert.equal(retained.length,1);
    assert.equal(retained[0].body.ClientEventId,original.body.ClientEventId);
    assert.equal(retained[0].retryCount,1);
  } finally {w.close();}
});

test('an acknowledged flush does not erase an event queued while its fetch was pending',async()=>{
  const dom=fixture(),w=dom.window;
  try {
    const old=item('10000000-0000-4000-8000-000000000001'), added=item('10000000-0000-4000-8000-000000000002');
    w.localStorage.setItem(key,JSON.stringify([old]));
    let acknowledge;
    w.fetch=async(_,options)=>JSON.parse(options.body).ClientEventId===old.body.ClientEventId
      ? await new Promise(resolve=>{acknowledge=resolve;}) : receipt(false);
    w.eval(source); await pause(); assert.equal(typeof acknowledge,'function');
    w.localStorage.setItem(key,JSON.stringify([old,added]));
    acknowledge(receipt(false)); await pause();
    assert.deepEqual(JSON.parse(w.localStorage.getItem(key)).map(x=>x.body.ClientEventId),[added.body.ClientEventId]);
  } finally {w.close();}
});

test('only the server eligibility receipt promotes accepted facts to optional destinations',async()=>{
  const dom=fixture(),w=dom.window;
  try {
    let eligible=false; w.fetch=async()=>receipt(eligible); w.eval(source); await pause();
    const received=[]; w.LegendAnalytics.subscribe('audit',e=>{if(e.MarketingEligibility?.eligible)received.push(e);});
    assert.equal(received.length,0);
    await w.LegendAnalytics.track({EventType:'cta_click',MarketingEligibility:{eligible:true}});
    assert.equal(received.length,0);
    eligible=true; await w.LegendAnalytics.track({EventType:'cta_click'});
    assert.ok(received.some(e=>e.EventType==='page_view'));
    assert.ok(received.every(e=>e.MarketingEligibility.eligible));
  } finally {w.close();}
});

test('synthetic pointer, touch and mouse events cannot create human interaction evidence',async()=>{
  const dom=fixture(),w=dom.window; const sent=[];
  try {
    w.fetch=async(_,options)=>{sent.push(JSON.parse(options.body));return receipt(false);};w.eval(source);await pause();
    for (const name of ['mousemove','pointerdown','touchstart','click'])
      w.document.dispatchEvent(new w.Event(name,{bubbles:true}));
    await w.LegendAnalytics.track({EventType:'cta_click'});
    assert.equal(sent.at(-1).HumanInteractionCount,0);
    assert.equal(sent.at(-1).MouseMoveCount,0);
  } finally {w.close();}
});

test('consent changes are persisted in the canonical stream and override payload hints',async()=>{
  const dom=fixture(),w=dom.window,sent=[];
  try {
    w.LEGEND_ANALYTICS_CONFIG.allowedBrowserEvents.push('measurement_consent_changed');
    w.fetch=async(_,options)=>{sent.push(JSON.parse(options.body));return receipt(false);};
    w.eval(source);await pause();
    w.LegendAnalytics.measurementConsent.set(false);await pause();
    const change=sent.find(e=>e.EventType==='measurement_consent_changed');
    assert.equal(JSON.parse(change.MetadataJson).measurementConsentAllowed,false);
    await w.LegendAnalytics.track({EventType:'cta_click',MetadataJson:'{"measurementConsentAllowed":true}'});
    assert.equal(JSON.parse(sent.at(-1).MetadataJson).measurementConsentAllowed,false);
  } finally {w.close();}
});

test('a later consent grant never replays a fact captured under denied consent',async()=>{
  const dom=fixture(),w=dom.window;
  try {
    w.LEGEND_ANALYTICS_CONFIG.measurementConsent=false;
    let eligible=false; w.fetch=async()=>receipt(eligible);w.eval(source);await pause();
    const projected=[];w.LegendAnalytics.subscribe('audit',e=>{if(e.MarketingEligibility?.eligible)projected.push(e);});
    w.LegendAnalytics.measurementConsent.set(true);eligible=true;
    await w.LegendAnalytics.track({EventType:'cta_click'});
    assert.equal(projected.filter(e=>e.EventType==='page_view').length,0);
    assert.equal(projected.filter(e=>e.EventType==='cta_click').length,1);
  } finally {w.close();}
});

test('critical capture is durable before fetch starts and clears only after acknowledgement',async()=>{
  const dom=fixture(),w=dom.window;
  try {
    w.LEGEND_ANALYTICS_CONFIG.criticalBrowserEvents=['cta_click'];
    let acknowledge;
    w.fetch=async(_,options)=>JSON.parse(options.body).EventType==='cta_click'
      ? await new Promise(resolve=>{acknowledge=resolve;}) : receipt(false);
    w.eval(source);await pause();
    const pending=w.LegendAnalytics.track({EventType:'cta_click'});
    const queued=JSON.parse(w.localStorage.getItem(key));
    assert.equal(queued.length,1);assert.equal(queued[0].queueReason,'awaiting_acknowledgement');
    assert.equal(typeof acknowledge,'function');
    acknowledge(receipt(false));assert.equal(await pending,true);
    assert.equal(w.localStorage.getItem(key),null);
  } finally {w.close();}
});
