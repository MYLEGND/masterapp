const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');
const {webcrypto} = require('node:crypto');
const repo=path.resolve(process.argv[2] || process.cwd());
const script=fs.readFileSync(path.join(repo,'Protect-Website/wwwroot/js/tracking.js'),'utf8');
const catalog=fs.readFileSync(path.join(repo,'SHARED/Analytics/AnalyticsEventCatalog.cs'),'utf8');
const browserEvents=[...catalog.matchAll(/Define\("([^"]+)"[^\n]+allowBrowser: true/g)].map(m=>m[1]);
const config={allowedBrowserEvents:browserEvents,criticalBrowserEvents:browserEvents};
const storage=()=>{const m=new Map();return {getItem:k=>m.get(k)||null,setItem:(k,v)=>m.set(k,String(v)),removeItem:k=>m.delete(k)}};
function boot({ajax=false,localStorage=storage(),sessionStorage=storage()}={}){
 const events=[], beacons=[];
 class Element {constructor(){this.dataset={};this.listeners=new Map();} addEventListener(k,f){this.listeners.set(k,[...(this.listeners.get(k)||[]),f]);} getAttribute(k){return k==='data-form-key'?'quote_auto_form':null;} dispatch(type,target=this,options={}){const event={type,target,persisted:!!options.persisted,defaultPrevented:false,preventDefault(){this.defaultPrevented=true}};for(const f of this.listeners.get(type)||[])f(event);return event;} }
 class Input extends Element{constructor(name,type='hidden'){super();this.name=name;this.type=type;this.value='';}}
 const form=new Element(); form.dataset={formKey:'quote_auto_form',ajaxSubmit:ajax?'true':undefined};form.getAttribute=k=>k==='data-form-key'?'quote_auto_form':k==='data-ajax-submit'?(ajax?'true':null):null;
 const fieldNames=['SessionId','VisitorId','UtmSource','UtmMedium','UtmCampaign','UtmId','UtmTerm','UtmContent','MetaCampaignId','MetaAdSetId','MetaAdId','Fbclid','ReferrerUrl','LandingPageUrl'];
 const fields=Object.fromEntries(fieldNames.map(n=>[n,new Input(n)]));form.elements={namedItem:n=>fields[n]||null};form.querySelector=sel=>{const m=sel.match(/name=["']([^"']+)["']/);return m?fields[m[1]]||null:null};form.querySelectorAll=()=>[];
 const doc=new Element();Object.assign(doc,{body:{dataset:{pageKey:'quote_auto',pageCategory:'quote',quoteType:'auto'},scrollHeight:1000,offsetHeight:1000},documentElement:{clientWidth:1280,clientHeight:720,scrollHeight:1000,offsetHeight:1000},referrer:'https://referrer.example.test/',visibilityState:'visible',querySelectorAll:s=>s==='form[data-form-key]'?[form]:[],querySelector:s=>s.startsWith('form[data-form-key=')?form:null,getElementById:()=>null});
 const win=new Element();Object.assign(win,{document:doc,location:{hostname:'protect.mylegnd.com',host:'protect.mylegnd.com',href:'https://protect.mylegnd.com/Quote/Auto?utm_source=qa_source&utm_medium=qa_medium',origin:'https://protect.mylegnd.com',pathname:'/Quote/Auto',search:'?utm_source=qa_source&utm_medium=qa_medium'},localStorage,sessionStorage,LEGEND_ANALYTICS_CONFIG:config,innerWidth:1280,innerHeight:720,screen:{width:1280,height:720},fetch:async(url,init)=>{events.push(JSON.parse(init.body));return {ok:true,status:200};},setTimeout:()=>0});
 const nav={userAgent:'Mozilla/5.0 Chrome/152',language:'en-US',webdriver:false,sendBeacon:(url,blob)=>{beacons.push(blob.text().then(JSON.parse));return true}};
 const sandbox={window:win,document:doc,navigator:nav,crypto:webcrypto,console,URL,URLSearchParams,Intl,Blob,sessionStorage:win.sessionStorage,localStorage:win.localStorage,performance:{now:()=>0},setTimeout:()=>0,setInterval:()=>0,clearTimeout(){},clearInterval(){},queueMicrotask,requestAnimationFrame:()=>0,HTMLElement:Element,HTMLInputElement:Input};
 vm.runInNewContext(script,sandbox);
 const focus=()=>form.dispatch('focusin',new Input('FirstName','text'));
 const exit=(persisted=false)=>win.dispatch('pagehide',win,{persisted});
 const flush=async()=>{await new Promise(setImmediate);return [...events,...await Promise.all(beacons)]};
 return {win,doc,form,fields,focus,exit,flush};
}
(async()=>{
 const results=[];
 let p=boot();p.focus();p.exit();p.exit();let e=await p.flush();results.push({case:'incomplete form exits twice',abandon:e.filter(x=>x.EventType==='form_abandon').length,stage:e.filter(x=>x.EventType==='form_abandon').map(x=>JSON.parse(x.MetadataJson).stage)});
 p=boot();p.exit();e=await p.flush();results.push({case:'untouched form exits',abandon:e.filter(x=>x.EventType==='form_abandon').length});
 p=boot();p.focus();p.form.addEventListener('submit',()=>p.form._trackSubmitAttempt?.(true,0));p.form.dispatch('submit');await p.flush();p.exit();e=await p.flush();results.push({case:'native submit with existing quote callback',attempts:e.filter(x=>x.EventType==='form_submit_attempt').length,abandon:e.filter(x=>x.EventType==='form_abandon').length});
 p=boot({ajax:true});p.focus();p.form._trackSubmitAttempt?.(true,0);await p.win.LegendAnalytics.track({EventType:'form_submit_attempt',FormKey:'quote_auto_form',SubmitOutcome:'error'});p.exit();e=await p.flush();results.push({case:'failed Life-style AJAX submit then exits',abandon:e.filter(x=>x.EventType==='form_abandon').length});
 p=boot();p.focus();await p.win.LegendAnalytics.track({EventType:'lead_form_submit_success',FormKey:'quote_auto_form'});p.exit();e=await p.flush();results.push({case:'confirmed success exits',abandon:e.filter(x=>x.EventType==='form_abandon').length});
 p=boot();p.focus();p.win.legendFormTracking.markSubmitted('quote_auto_form','server_confirmed_inquiry');p.exit();e=await p.flush();results.push({case:'server-confirmed AJAX completion exits',abandon:e.filter(x=>x.EventType==='form_abandon').length,successEvents:e.filter(x=>x.EventType==='lead_form_submit_success'||x.EventType==='form_submit_success').length});
 p=boot();p.focus();p.doc.visibilityState='hidden';p.doc.dispatch('visibilitychange');p.doc.visibilityState='visible';p.doc.dispatch('visibilitychange');e=await p.flush();results.push({case:'tab hide and return',abandon:e.filter(x=>x.EventType==='form_abandon').length});
 p=boot();results.push({case:'canonical hidden attribution bound',sessionPresent:!!p.fields.SessionId.value,visitorPresent:!!p.fields.VisitorId.value,utmSource:p.fields.UtmSource.value,utmMedium:p.fields.UtmMedium.value});
 p=boot();p.focus();p.exit(true);e=await p.flush();results.push({case:'BFCache persisted pagehide',abandon:e.filter(x=>x.EventType==='form_abandon').length});
 const first=boot();first.focus();await first.flush();p=boot({localStorage:first.win.localStorage,sessionStorage:first.win.sessionStorage});p.focus();p.exit();e=await p.flush();results.push({case:'same-session reload resumes lifecycle without duplicate start',starts:e.filter(x=>x.EventType==='form_start').length,abandon:e.filter(x=>x.EventType==='form_abandon').length});
 const expected=[{abandon:1,stage:['started']},{abandon:0},{attempts:1,abandon:0},{abandon:1},{abandon:0},{abandon:0,successEvents:0},{abandon:0},{sessionPresent:true,visitorPresent:true,utmSource:'qa_source',utmMedium:'qa_medium'},{abandon:0},{starts:0,abandon:1}];
 results.forEach((r,i)=>{const {case:label,...actual}=r;assert.deepEqual(actual,expected[i],label)});
 console.log(JSON.stringify({passed:results.length,results},null,2));
})().catch(e=>{console.error(e);process.exitCode=1});
