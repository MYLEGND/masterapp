import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/page-health.js',import.meta.url),'utf8');
function fixture(fetch) {
  const listeners=new Map();
  const window={fetch,location:{host:'localhost',hostname:'localhost',pathname:'/founder/legend-connect',origin:'http://localhost'},addEventListener:(name,fn)=>listeners.set(name,fn)};
  const context={window,document:{title:'Connect',body:null,readyState:'loading',addEventListener(){}},URL,Error,console:{warn(){},error(){}},localStorage:{getItem:()=>null,setItem(){}},navigator:{}};
  vm.createContext(context);
  const code=source.replace('window.LegendPageHealth = Object.freeze({ current });','window.LegendPageHealth = Object.freeze({ current }); window.testState = state; window.testRequests = settledRequests;');
  vm.runInContext(code,context);
  return {window,state:window.testState,report:()=>JSON.parse(window.LegendPageHealth.current.exportReport()),listeners,reload:()=>vm.runInContext(code,context)};
}
const ok={ok:true,status:200};const fail={ok:false,status:500,statusText:'Failure'};
function deferred(){let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b});return {promise,resolve,reject};}
test('deliberate signal cancellation is rethrown unchanged without a current issue or global duplicate',async()=>{
  const controller=new AbortController();controller.abort();const error=controller.signal.reason;
  const f=fixture(async()=>{throw error});await assert.rejects(f.window.fetch('/data',{signal:controller.signal}),e=>e===error);
  f.listeners.get('unhandledrejection')({reason:error});assert.equal(f.state.events.length,0);
});
test('timeout remains visible even with aborted signal',async()=>{
  const error=new DOMException('Deadline exceeded','TimeoutError');const controller=new AbortController();controller.abort(error);
  const f=fixture(async()=>{throw error});await assert.rejects(f.window.fetch('/data',{signal:controller.signal}));assert.equal(f.state.events.length,1);
});
test('unrelated transport error racing cancellation remains visible and is not double counted globally',async()=>{
  const controller=new AbortController();controller.abort();const error=new TypeError('Failed to fetch');
  const f=fixture(async()=>{throw error});await assert.rejects(f.window.fetch('/data',{signal:controller.signal}));f.listeners.get('unhandledrejection')({reason:error});assert.equal(f.state.events.length,1);assert.equal(f.state.events[0].sessionCount,1);
});
test('HTTP failure recovers on same request success while learned history remains',async()=>{
  let response=fail;const f=fixture(async()=>response);await f.window.fetch('/data?q=one');assert.equal(f.state.events.length,1);response=ok;
  await f.window.fetch('/data?q=one');assert.equal(f.state.events.length,0);assert.equal(Object.keys(f.report().learnedPatterns).length,1);
});
test('recovery is scoped to method and query, with transport retry recovery',async()=>{
  let broken=true;const f=fixture(async()=>{if(broken)throw new TypeError('Failed to fetch');return ok;});
  await assert.rejects(f.window.fetch('/data?q=one'));broken=false;await f.window.fetch('/data?q=two');await f.window.fetch('/data?q=one',{method:'POST'});assert.equal(f.state.events.length,1);await f.window.fetch('/data?q=one');assert.equal(f.state.events.length,0);
});
test('older success cannot clear a newer failure',async()=>{
  const old=deferred();let calls=0;const f=fixture(()=>++calls===1?old.promise:Promise.resolve(fail));const pending=f.window.fetch('/data');await f.window.fetch('/data');old.resolve(ok);await pending;assert.equal(f.state.events.length,1);
});
test('older failure cannot resurrect a recovered current issue',async()=>{
  const old=deferred();let calls=0;const f=fixture(()=>++calls===1?old.promise:Promise.resolve(ok));const pending=f.window.fetch('/data');await f.window.fetch('/data');old.resolve(fail);await pending;assert.equal(f.state.events.length,0);
});
test('errors update current count without opening or reopening the drawer',async()=>{
  const f=fixture(async()=>fail);await f.window.fetch('/data');assert.equal(f.state.drawerOpen,false);f.window.LegendPageHealth.current.open();assert.equal(f.state.drawerOpen,true);f.window.LegendPageHealth.current.close();await f.window.fetch('/data');assert.equal(f.state.drawerOpen,false);assert.equal(f.state.events[0].sessionCount,2);
});
test('script reload wraps once and diagnostics never retry requests',async()=>{
  let calls=0;const f=fixture(async()=>{calls++;return fail});const wrapped=f.window.fetch;f.reload();assert.equal(f.window.fetch,wrapped);await f.window.fetch('/data');assert.equal(calls,1);assert.equal(f.state.events.length,1);
});
test('unhandled unrelated script errors remain visible and status is accessible',()=>{
  const f=fixture(async()=>ok);f.listeners.get('unhandledrejection')({reason:new Error('Application broken')});assert.equal(f.state.events.length,1);assert.match(source,/legend-page-health-status" role="status" aria-live="polite"/);
});
test('both hosts and client workspace initialize shared diagnostics once; Explore uses existing owner without moving it',()=>{
  for(const file of ['AgentPortal/Views/Shared/_Layout.cshtml','AgentPortal/Views/Shared/_ClientWorkspaceLayout.cshtml','ClientApp/Views/Shared/_Layout.cshtml']) {
    const layout=readFileSync(new URL('../../'+file,import.meta.url),'utf8');
    assert.equal(layout.split('~/Views/Diagnostics/_PageHealth.cshtml').length-1,1);
  }
  const layout=readFileSync(new URL('../../AgentPortal/Views/Shared/_Layout.cshtml',import.meta.url),'utf8');
  assert.match(layout,/window\.LegendPageHealth\?\.current.open\(\)/);
  assert.doesNotMatch(layout,/relocatePageHealthTrigger|normalizePageHealthInsideExplore|pageHealthElementCandidates/);
});

test('more than 128 settled endpoints cannot evict ordering protection for an outstanding request',async()=>{
  const old=deferred();let calls=0;
  const f=fixture(url=>url==='/data' && ++calls===1?old.promise:Promise.resolve(ok));
  const pending=f.window.fetch('/data');await f.window.fetch('/data');
  for(let i=0;i<160;i++)await f.window.fetch(`/other/${i}`);
  assert.equal(f.window.testRequests.size,1);
  old.resolve(fail);await pending;
  assert.equal(f.state.events.length,0);
  assert.equal(f.window.testRequests.size,0);
});
test('cancelled and failed requests release completed ordering state',async()=>{
  const controller=new AbortController();controller.abort();
  const f=fixture(async()=>{throw controller.signal.reason});
  await assert.rejects(f.window.fetch('/cancelled',{signal:controller.signal}));
  assert.equal(f.window.testRequests.size,0);
  await assert.rejects(f.window.fetch('/failed'));
  assert.equal(f.state.events.length,1);
  assert.equal(f.window.testRequests.size,0);
});
