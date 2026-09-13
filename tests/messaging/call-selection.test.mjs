import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const source=readFileSync(new URL('../../SHARED/wwwroot/js/messaging.js',import.meta.url),'utf8');
function implementation(name){const start=source.search(new RegExp(`^  (?:async )?function ${name}\\(`,'m'));assert.notEqual(start,-1);const tail=source.slice(start+1);const end=tail.search(/\n  (?:async )?function /);return end<0?tail:tail.slice(0,end);}
function fixture(request=async()=>({conversation:{id:'created'}})) {
 const calls=[],errors=[],nodes=new Map();const document={getElementById:id=>{if(!nodes.has(id))nodes.set(id,{});return nodes.get(id);}};
 const c={state:{callSelection:null,callSelectionVersion:0,realtime:{state:'Connected'},callClient:{start:async(...args)=>calls.push(args)}},document,window:{setTimeout,clearTimeout},AbortController,applicationCopy:v=>v,request,showError:e=>errors.push(e),renderConversations(){},renderSearchResults(){},loadRecipients:async()=>{},elements:{search:{value:'',focus(){}}},loadConversation:async id=>calls.push(['open',id])};vm.createContext(c);for(const n of ['isDirectCallChoice','cancelCallSelection','selectConversationForCurrentIntent','startSelectedCall','beginCallSelection'])vm.runInContext(implementation(n),c);return {c,calls,errors,nodes};
}
for(const video of [false,true])test(`${video?'video':'voice'} selected before recipient starts the chosen call with no second chooser`,async()=>{
 const {c,calls}=fixture();c.beginCallSelection(video);await c.startSelectedCall('existing',{displayName:'Recipient'});assert.deepEqual(calls,[['existing',video,'Recipient']]);assert.equal(c.state.callSelection,null);
});
test('new contact reuses protected conversation creation without sending placeholder text',async()=>{
 let seen;const {c,calls}=fixture(async(url,options)=>{seen={url,options};return {conversation:{id:'created'}};});c.beginCallSelection(true);await c.startSelectedCall(null,{contactKey:'protected',displayName:'Recipient'});assert.equal(seen.url,'/Messaging/Conversations');assert.deepEqual(JSON.parse(seen.options.body),{contactKey:'protected',body:null,subject:null,includeMessages:false});assert.deepEqual(calls,[['created',true,'Recipient']]);
});
test('repeat contact taps share one pending creation and one call',async()=>{
 let resolve,requests=0;const held=new Promise(r=>resolve=r);const {c,calls}=fixture(()=>{requests++;return held;});c.beginCallSelection(false);const first=c.startSelectedCall(null,{contactKey:'protected'});await c.startSelectedCall(null,{contactKey:'other'});assert.equal(requests,1);resolve({conversation:{id:'created'}});await first;assert.equal(calls.length,1);
});
test('cancelling recipient selection prevents a late creation response from dialing',async()=>{
 let resolve;const held=new Promise(r=>resolve=r);const {c,calls}=fixture(()=>held);c.beginCallSelection(true);const pending=c.startSelectedCall(null,{contactKey:'protected'});c.cancelCallSelection();resolve({conversation:{id:'created'}});await pending;assert.equal(calls.length,0);
});
test('unavailable connection never creates a thread or silently starts another call type',async()=>{
 let requests=0;const {c,calls,errors}=fixture(()=>{requests++;});c.beginCallSelection(true);c.state.realtime.state='Disconnected';await c.startSelectedCall(null,{contactKey:'protected'});assert.equal(requests,0);assert.equal(calls.length,0);assert.equal(errors[0],'Calling is unavailable.');
});
test('ordinary conversation selection remains navigation and group/assistant choices stay out of direct calling',async()=>{
 const {c,calls}=fixture();c.selectConversationForCurrentIntent({id:'conversation'});assert.deepEqual(calls,[['open','conversation']]);assert.equal(c.isDirectCallChoice({conversationType:'Group'}),false);assert.equal(c.isDirectCallChoice({conversationType:'Assistant'}),false);assert.equal(c.isDirectCallChoice({isClosed:true}),false);assert.equal(c.isDirectCallChoice({conversationType:'ClientAgent'}),true);
});

test('recipient picker retires before the existing call UI awaits microphone permission',async()=>{
 let resolve;const held=new Promise(r=>resolve=r);const {c,calls,nodes}=fixture();c.state.callClient.start=(...args)=>{calls.push(args);return held;};c.beginCallSelection(true);const pending=c.startSelectedCall('existing',{displayName:'Recipient'});assert.equal(c.state.callSelection,null);assert.equal(nodes.get('messagingCallSelection').hidden,true);await c.startSelectedCall('other',{});assert.equal(calls.length,1);resolve();await pending;
});
test('dismissed picker cannot show a late recipient-directory failure',async()=>{
 let reject;const held=new Promise((_,r)=>reject=r);const {c,errors}=fixture();c.loadRecipients=()=>held;c.beginCallSelection(false);c.cancelCallSelection();reject(new Error('late directory failure'));await held.catch(()=>{});await Promise.resolve();assert.deepEqual(errors,[]);
});
