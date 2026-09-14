import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/messaging.js',import.meta.url),'utf8');
function implementation(name){const start=source.search(new RegExp(`^  (?:async )?function ${name}\\(`,'m'));assert.notEqual(start,-1);const tail=source.slice(start+1);const end=tail.search(/\n  (?:async )?function /);return end<0?tail:tail.slice(0,end);}
function fixture(invoke){
 const pills=[]; const timers=new Map();let id=0;
 const c={state:{isOpen:true,presenceGeneration:0,presence:null,realtime:{state:'Connected',invoke}},document:{hidden:false},root:{querySelectorAll:()=>pills},window:{innerHeight:900,setTimeout:f=>{timers.set(++id,f);return id;},clearTimeout:i=>timers.delete(i)},applicationCopy:v=>v,participantIdentityKey:(id,type)=>`${type?.toLowerCase()}:${id?.toLowerCase()}`,console:{warn(){}}};
 vm.createContext(c);for(const name of ['renderPresence','clearPresence','refreshPresence'])vm.runInContext(implementation(name),c);
 return {c,timers,pill(dataset){const classes=new Map();const p={dataset,parentElement:{getBoundingClientRect:()=>({width:100,height:30,top:20,bottom:50})},classList:{toggle:(key,value)=>classes.set(key,value)},classes};pills.push(p);return p;}};
}
test('presence comes only from typed authorized server observations; omitted contacts stay unknown',async()=>{
 const {c,pill}=fixture(async()=>({participants:[{userId:'same',participantType:'Client',isOnline:true}],conversations:[{conversationId:'group',isOnline:false}]}));
 const client=pill({presenceUser:'same',presenceType:'Client'}),agent=pill({presenceUser:'same',presenceType:'Agent'}),group=pill({presenceConversation:'group'});
 await c.refreshPresence();assert.equal(client.textContent,'Online');assert.equal(group.textContent,'Offline');assert.equal(agent.hidden,true);assert.equal(agent.textContent,'');
});
test('hidden or closed messaging sends only an empty heartbeat and displays no presence',async()=>{
 let request;const {c,pill}=fixture(async(_,r)=>{request=r;return {participants:[],conversations:[]};});const p=pill({presenceUser:'private',presenceType:'Client'});
 c.state.isOpen=false;await c.refreshPresence();assert.equal(request.participants.length,0);assert.equal(request.conversationIds.length,0);assert.equal(p.hidden,true);
 c.state.isOpen=true;c.document.hidden=true;await c.refreshPresence();assert.equal(request.participants.length,0);
});
test('failure clears prior Online without inventing Offline',async()=>{
 const {c,pill}=fixture(async()=>{throw Error('transport');});const p=pill({presenceConversation:'g'});c.state.presence={conversations:[{conversationId:'g',isOnline:true}]};c.renderPresence();assert.equal(p.textContent,'Online');await c.refreshPresence();assert.equal(p.hidden,true);assert.equal(p.textContent,'');
});
test('expired or disconnected in-flight replies cannot restore stale Online',async()=>{
 let resolve;const held=new Promise(r=>resolve=r);const {c,pill,timers}=fixture(()=>held);const p=pill({presenceConversation:'g'});const running=c.refreshPresence();[...timers.values()][0]();resolve({conversations:[{conversationId:'g',isOnline:true}]});await running;assert.equal(p.hidden,true);assert.equal(c.state.presence,null);
});
test('concurrent refreshes share transport and retain one trailing refresh',async()=>{
 let resolve,calls=0;const held=new Promise(r=>resolve=r);const {c}=fixture(()=>{calls++;return calls===1?held:Promise.resolve({participants:[],conversations:[]});});
 const first=c.refreshPresence();await c.refreshPresence();await c.refreshPresence();assert.equal(calls,1);resolve({participants:[],conversations:[]});await first;await new Promise(r=>setImmediate(r));assert.equal(calls,2);
});
test('presence bounds and deduplicates visible targets',async()=>{
 let request;const {c,pill}=fixture(async(_,r)=>{request=r;return {};});for(let i=0;i<70;i++){pill({presenceConversation:String(i)});pill({presenceUser:String(i),presenceType:'Client'});}pill({presenceConversation:'0'});await c.refreshPresence();assert.equal(request.participants.length,50);assert.equal(request.conversationIds.length,50);
});

test('selected thread is prioritized over a long inbox and offscreen contacts are excluded',async()=>{
 let request;const {c,pill}=fixture(async(_,r)=>{request=r;return {};});c.state.active={id:'selected'};for(let i=0;i<60;i++)pill({presenceConversation:String(i)});const hidden=pill({presenceUser:'offscreen',presenceType:'Client'});hidden.parentElement.getBoundingClientRect=()=>({width:100,height:30,top:950,bottom:980});await c.refreshPresence();assert.equal(request.conversationIds[0],'selected');assert.equal(request.conversationIds.length,50);assert.equal(request.participants.length,0);
});
test('malformed isOnline does not manufacture Offline',async()=>{
 const {c,pill}=fixture(async()=>({conversations:[{conversationId:'g'}]}));const p=pill({presenceConversation:'g'});await c.refreshPresence();assert.equal(p.hidden,true);assert.equal(p.textContent,'');
});
