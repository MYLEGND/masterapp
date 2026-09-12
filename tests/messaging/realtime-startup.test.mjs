import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const liveSource=readFileSync(new URL('../../AgentPortal/wwwroot/js/live-sync.js',import.meta.url),'utf8');
const leadsSource=readFileSync(new URL('../../AgentPortal/wwwroot/js/rebuttals-leads.js',import.meta.url),'utf8');
const partial=readFileSync(new URL('../../SHARED/Views/Messaging/_SignalRClient.cshtml',import.meta.url),'utf8');
function fixture(fails=false) {
  const callbacks=new Map(), handlers=new Map();let builds=0,starts=0;const retries=[];
  const connection={on:(event,fn)=>handlers.set(event,fn),onreconnecting(){},onreconnected(){},onclose(){},start:()=>{starts++;return fails?Promise.reject(new Error('offline')):Promise.resolve();},invoke:()=>Promise.resolve()};
  class HubConnectionBuilder { withUrl(){return this;} withAutomaticReconnect(){return this;} build(){builds++;return connection;} }
  const context={window:{addEventListener:(event,fn)=>callbacks.set(event,fn)},console:{info(){},warn(){},error(){}},setTimeout:fn=>retries.push(fn)};
  vm.createContext(context);
  return {context,callbacks,handlers,retries,install:()=>context.window.signalR={HubConnectionBuilder},builds:()=>builds,starts:()=>starts};
}
test('shared SignalR script cannot block HTML parsing and publishes one readiness event',()=>{
  assert.match(partial,/\sasync\s/);
  assert.match(partial,/onload="window\.dispatchEvent\(new Event\('legend-signalr-ready'\)\)"/);
  assert.equal((partial.match(/<script\b/g)||[]).length,1);
});
test('live-sync API registers while CDN is absent and starts once when ready',async()=>{
  const f=fixture();vm.runInContext(liveSource,f.context);
  assert(f.context.window.liveSync);assert.equal(f.builds(),0);
  let received;f.context.window.liveSync.onCall((...value)=>received=value);
  assert.doesNotThrow(()=>f.context.window.liveSync.sendCall('lead',1));
  f.install();f.callbacks.get('legend-signalr-ready')();f.callbacks.get('legend-signalr-ready')();
  assert.equal(f.builds(),1);assert.equal(f.starts(),1);
  f.handlers.get('callUpdated')('lead',2);assert.deepEqual(received,['lead',2]);
});
test('already-loaded SignalR starts immediately and failed connection retains existing backoff',async()=>{
  const f=fixture(true);f.install();vm.runInContext(liveSource,f.context);
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(f.starts(),1);assert.equal(f.retries.length,1);
  f.callbacks.get('legend-signalr-ready')();assert.equal(f.starts(),1);
});
test('lead bridge initialization tolerates missing library and starts exactly once later',async()=>{
  const f=fixture();f.context.leadBridgeConnection=null;f.context.applyRemoteState=()=>{};let refreshes=0;f.context.fetchActiveState=()=>{refreshes++;};
  const start=leadsSource.indexOf('    function startLeadBridgeRealtime()');
  const end=leadsSource.indexOf("    window.addEventListener('legend-signalr-ready'",start);
  assert(start>=0&&end>start);
  vm.runInContext(leadsSource.slice(start,end),f.context);
  assert.doesNotThrow(()=>f.context.startLeadBridgeRealtime());assert.equal(f.builds(),0);
  f.install();f.context.startLeadBridgeRealtime();f.context.startLeadBridgeRealtime();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(f.starts(),1);assert.equal(refreshes,1);
  assert.match(leadsSource.slice(end),/startLeadBridgeRealtime\(\);\s+try \{\s+baseLeads = await fetchLeads\(\)/);
});
