import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';
const source=readFileSync(new URL('../../SHARED/WebsitePlatform/tracking.js',import.meta.url),'utf8');
const partial=readFileSync(new URL('../../ParfaitApp/Views/Shared/_ParfaitCommerceTracking.cshtml',import.meta.url),'utf8');
function boot({scope='agent:a',query='',snapshot={},now=Date.now()}={}) {
  const dom=new JSDOM('<body data-page-key="home"></body>',{url:'https://public.example.com/'+query,runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window,events=[];
  for(const [key,value] of Object.entries(snapshot))w.localStorage.setItem(key,value);
  w.Date.now=()=>now;
  w.LEGEND_ANALYTICS_CONFIG={attributionScope:scope,allowedBrowserEvents:['page_view','cta_click','Backtrack'],criticalBrowserEvents:[]};
  w.fetch=async(_url,options)=>{events.push(JSON.parse(options.body));return {ok:true,status:200};};
  w.eval(source);
  return {dom,w,events,snapshot:()=>Object.fromEntries(Object.entries(w.localStorage)),advance(ms){now+=ms;}};
}
test('new session is direct while scoped first-touch and legacy historical storage remain unchanged',()=>{
  const now=Date.now(),legacy=JSON.stringify({oppref:'legacy',utmCampaign:'legacy-ad'});
  const paid=boot({now,query:'?oppref=first-click&utm_campaign=first-ad',snapshot:{legend_attr_first_touch:legacy}});
  const history=paid.snapshot(),sid=paid.w.LegendAnalytics.ids.getSessionId();paid.dom.window.close();
  const direct=boot({now:now+31*60000,snapshot:history});
  try {
    assert.notEqual(direct.w.LegendAnalytics.ids.getSessionId(),sid);
    assert.equal(direct.events[0].Oppref,null);
    assert.equal(direct.events[0].UtmCampaign,null);
    assert.equal(direct.w.LegendAnalytics.ids.getFirstTouchAttribution().oppref,'first-click');
    assert.equal(direct.w.localStorage.getItem('legend_attr_first_touch'),legacy);
    assert.equal(direct.w.localStorage.getItem('legend_attr_first_touch:agent%3Aa'),history['legend_attr_first_touch:agent%3Aa']);
  }finally{direct.dom.window.close();}
});
test('same-host owner switch never inherits campaign, session, or first touch',()=>{
  const a=boot({query:'?fbclid=agent-a-click&utm_campaign=agent-a'}),history=a.snapshot();a.dom.window.close();
  const b=boot({scope:'agent:b',snapshot:history});
  try {
    assert.equal(b.events[0].Fbclid,null);
    assert.equal(b.events[0].UtmCampaign,null);
    assert.equal(b.w.LegendAnalytics.ids.getFirstTouchAttribution().fbclid,null);
    assert.notEqual(b.events[0].SessionId,history['legend_session_id:agent%3Aa']);
  }finally{b.dom.window.close();}
});
test('fresh query replaces old campaign without mixing provider IDs and preserves first-touch history',()=>{
  const a=boot({query:'?fbclid=old-meta-click&utm_campaign=original'}),history=a.snapshot();a.dom.window.close();
  const b=boot({snapshot:history,query:'?oppref=new-openai-click&utm_campaign=current'});
  try {
    assert.equal(b.events[0].Oppref,'new-openai-click');
    assert.equal(b.events[0].UtmCampaign,'current');
    assert.equal(b.events[0].Fbclid,null);
    assert.equal(b.w.LegendAnalytics.ids.getFirstTouchAttribution().fbclid,'old-meta-click');
  }finally{b.dom.window.close();}
});
test('session activity refreshes timeout, then expiry clears current attribution without changing history',()=>{
  const a=boot({query:'?oppref=landing-click'}),history=a.snapshot();a.dom.window.close();
  const b=boot({snapshot:history});
  try {
    const sid=b.w.LegendAnalytics.ids.getSessionId();
    b.advance(20*60000);assert.equal(b.w.LegendAnalytics.ids.getAttribution().oppref,'landing-click');
    b.advance(20*60000);assert.equal(b.w.LegendAnalytics.ids.getSessionId(),sid);
    assert.equal(b.w.LegendAnalytics.ids.getAttribution().oppref,'landing-click');
    b.advance(31*60000);assert.equal(b.w.LegendAnalytics.ids.getAttribution().oppref,null);
    assert.notEqual(b.w.LegendAnalytics.ids.getSessionId(),sid);
    assert.equal(b.w.LegendAnalytics.ids.getFirstTouchAttribution().oppref,'landing-click');
  }finally{b.dom.window.close();}
});
test('unresolved owner continues first-party events but cannot inherit stored attribution',()=>{
  const a=boot({scope:null,query:'?oppref=unresolved-click'}),history=a.snapshot();a.dom.window.close();
  const b=boot({scope:null,snapshot:history});
  try{assert.equal(b.events[0].EventType,'page_view');assert.equal(b.events[0].Oppref,null);}
  finally{b.dom.window.close();}
});
test('store cookie writes current scoped session and clears prior paid identifiers on direct new session',()=>{
  const a=boot({query:'?oppref=paid-click&utm_campaign=paid'}),history=a.snapshot();a.dom.window.close();
  const b=boot({snapshot:history,now:Date.now()+31*60000});
  try {
    b.w.document.cookie='pf_attribution='+encodeURIComponent(JSON.stringify({oppref:'unscoped-old'}))+'; Path=/';
    const start=partial.indexOf(' const ids=window.LegendAnalytics?.ids;'),end=partial.indexOf('\n})();',start);
    const writer=partial.slice(start,end).replace(/const storeScope=.*;/,'const storeScope="canonical-store";');
    b.w.eval('(()=>{'+writer+'})();');
    const cookie=()=>JSON.parse(decodeURIComponent(b.w.document.cookie.split('; ').find(v=>v.startsWith('pf_attribution=')).slice('pf_attribution='.length)));
    const first=cookie();assert.equal(first.scope,'canonical-store');assert.deepEqual(first.attribution,{});
    b.advance(20*60000);b.w.document.dispatchEvent(new b.w.Event('click'));
    const next=cookie();assert.equal(next.sessionId,first.sessionId);assert.ok(next.updatedAt>first.updatedAt);
  }finally{b.dom.window.close();}
});

test('Meta adapter respects empty canonical attribution and never reads or changes legacy history',async()=>{
  const old=JSON.stringify({utmCampaign:'foreign-campaign',fbclid:'foreign-click'});
  const f=boot({snapshot:{legend_attr_session:old,legend_attr_first_touch:old}});
  try {
    f.w.sessionStorage.setItem('legend_attr_session',old);
    f.w.eval(readFileSync(new URL('../../SHARED/WebsitePlatform/meta-signal-intelligence.js',import.meta.url),'utf8'));
    const session=f.w.metaSignalIntelligence.createLandingSession({enabled:true,sendBrowserEvents:false,sendServerEvents:true,persistEvents:true,siteKey:'protect',pageKey:'home',browserSignalEventNames:['Backtrack']});
    await session.trackBacktrack();
    const event=f.events.find(row=>row.EventType==='Backtrack');
    assert.ok(event);assert.equal(event.Fbclid,null);
    assert.equal(event.MetaSignal.attribution.fbclid,null);
    assert.equal(event.MetaSignal.attribution.utmCampaign,null);
    assert.equal(f.w.localStorage.getItem('legend_attr_session'),old);
    assert.equal(f.w.sessionStorage.getItem('legend_attr_session'),old);
    assert.equal(f.w.localStorage.getItem('legend_attr_first_touch'),old);
  }finally{f.dom.window.close();}
});
