import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../SHARED/wwwroot/js/application-localization.js', import.meta.url), 'utf8');
const flush = async () => { for (let i=0;i<6;i++) await Promise.resolve(); };
const entry = (text='Bonjou',failureCode=null) => ({id:'hello',source:'Hello',text,context:'visual interface copy',sourceRevision:'r1',placeholders:[],failureCode});
const next = overrides => ({disposition:'RetryableFailure',remainingEntries:1,retryAfterSeconds:15,maximumConsecutiveNoProgress:2,maximumDurationSeconds:180,maximumRequestsPerPass:64,cooldownSeconds:60,...overrides});
const catalog = (language='ht',continuation=null,entries=[entry()]) => ({catalogVersion:'v1',sourceLanguageCode:'en',languageCode:language,locale:language,entries,continuation,isComplete:continuation===null});
const response = value => ({ok:true,status:200,headers:{get:()=> 'application/json'},json:async()=>value});
function harness(fetch) {
  let now=0,id=0;const timers=new Map(),events=new Map();
  const publicNode={nodeValue:'Hello',parentElement:{closest:()=>false}};
  const privateNode={nodeValue:'Hello',parentElement:{closest:()=>true}};
  const document={visibilityState:'visible',readyState:'complete',documentElement:{lang:'en',dir:'ltr'},
    body:{querySelectorAll:()=>[],prepend(){}},createElement:()=>({setAttribute(){},remove(){}}),
    createTreeWalker:()=>{let n=0;return {nextNode:()=>[publicNode,privateNode][n++]};},
    addEventListener:(name,fn)=>events.set(name,fn)};
  const env={document,AbortController,Intl,Map,Set,WeakMap,NodeFilter:{SHOW_TEXT:1},
    Date:class extends Date { static now(){return now;} },
    MutationObserver:class {observe(){}},requestAnimationFrame:()=>{},fetch,
    setTimeout:(fn,ms)=>{timers.set(++id,{fn,at:now+ms});return id;},clearTimeout:key=>timers.delete(key),
    window:{addEventListener:(name,fn)=>events.set(name,fn)}};
  vm.runInNewContext(source,env);
  return {publicNode,privateNode,document,async event(name,detail){events.get(name)?.({detail});await flush();},
    async tick(ms){const target=now+ms;while(true){const due=[...timers].filter(([,v])=>v.at<=target).sort((a,b)=>a[1].at-b[1].at)[0];if(!due)break;now=due[1].at;timers.delete(due[0]);due[1].fn();await flush();}now=target;await flush();},timers};
}
test('final failed batch resumes on server instruction, preserves successful copy and excludes private text',async()=>{
 let calls=0;const h=harness(async()=>response(++calls===1?catalog('ht',next(),[entry('Hello','translation_provider_timeout')]):catalog()));await flush();
 assert.equal(h.publicNode.nodeValue,'Hello');await h.tick(15000);assert.equal(calls,2);assert.equal(h.publicNode.nodeValue,'Bonjou');assert.equal(h.privateNode.nodeValue,'Hello');
});
test('no-progress pass cools down and automatically rearms while foreground',async()=>{
 let calls=0;const h=harness(async()=>{calls++;return response(catalog('ht',next({retryAfterSeconds:1}),[entry('Hello','translation_pending')]));});await flush();
 await h.tick(2000);assert.equal(calls,3);await h.tick(59000);assert.equal(calls,3);await h.tick(1000);assert.equal(calls,4);
});
test('blocked configuration is terminal even when entries include pending work',async()=>{
 let calls=0;const h=harness(async()=>{calls++;return response(catalog('ht',{disposition:'Blocked',remainingEntries:1,retryAfterSeconds:null},[entry('Hello','translation_capacity_configuration_unavailable')]));});await flush();await h.tick(300000);assert.equal(calls,1);
});
test('cached language applies synchronously before revalidation and stale language cannot overwrite it',async()=>{
 let calls=0,resolveOld;const h=harness(async()=>{calls++;if(calls===1)return response(catalog());if(calls===2)return response(catalog('es',null,[entry('Hola')]));return new Promise(resolve=>{resolveOld=resolve;});});await flush();await h.event('legend:preferred-language-changed',{languageCode:'es'});assert.equal(h.publicNode.nodeValue,'Hola');
 await h.event('legend:preferred-language-changed',{languageCode:'ht'});assert.equal(h.publicNode.nodeValue,'Bonjou');const old=resolveOld;await h.event('legend:preferred-language-changed',{languageCode:'es'});old(response(catalog()));await flush();assert.equal(h.publicNode.nodeValue,'Hola');
});
test('first transport failure recovers without focus; terminal400 does not loop',async()=>{
 let calls=0;const h=harness(async()=>{if(++calls===1)throw new Error('offline');return response(catalog());});await flush();await h.tick(2000);assert.equal(calls,2);assert.equal(h.publicNode.nodeValue,'Bonjou');
 let badCalls=0;const bad=harness(async()=>{badCalls++;return {ok:false,status:400,headers:{get:()=>null}};});await flush();await bad.tick(300000);assert.equal(badCalls,1);
});
test('monthly capacity deadline survives focus/background and does not overflow browser timers',async()=>{
 let calls=0;const h=harness(async()=>{calls++;return response(catalog('ht',next({retryAfterSeconds:2678400}),[entry('Hello','translation_capacity_monthly_exhausted')]));});await flush();h.document.visibilityState='hidden';await h.event('visibilitychange');h.document.visibilityState='visible';await h.event('visibilitychange');await h.tick(2678399000);assert.equal(calls,1);await h.tick(1000);assert.equal(calls,2);
});
test('authorization clear restores source text and abort fences late response',async()=>{
 let calls=0;const h=harness(async()=>{if(++calls===1)return response(catalog());return {ok:false,status:401};});await flush();assert.equal(h.publicNode.nodeValue,'Bonjou');await h.event('focus');assert.equal(h.publicNode.nodeValue,'Hello');assert.equal(h.document.documentElement.lang,'en');await h.tick(300000);assert.equal(calls,2);
});
test('authoritative approval withdrawal replaces old cached text without guessing stale eligibility',async()=>{
 let calls=0;const h=harness(async()=>response(++calls===1?catalog():catalog('ht',{disposition:'AwaitingApproval',remainingEntries:1,retryAfterSeconds:null},[entry('Hello','approved_translation_unavailable')])));await flush();assert.equal(h.publicNode.nodeValue,'Bonjou');await h.event('focus');assert.equal(h.publicNode.nodeValue,'Hello');await h.tick(300000);assert.equal(calls,2);
});
test('invalid successful JSON response stops instead of becoming a recurring transport retry',async()=>{
 let calls=0;const h=harness(async()=>{calls++;return {ok:true,status:200,headers:{get:()=> 'application/json'},json:async()=>{throw new SyntaxError('malformed');}};});await flush();await h.tick(300000);assert.equal(calls,1);
});
