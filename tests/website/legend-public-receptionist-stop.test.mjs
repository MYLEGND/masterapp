import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

// Executes the shipped full browser script, with deterministic local media/network/timers.
// No provider, credentials, DOM package or real microphone is used.
const source = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const settle = async () => { for(let i=0;i<20;i++) await Promise.resolve(); };
function fixture({failStatus=false, lateStart=false}={}) {
  const events=new Map(), timers=new Map(), requests=[], peers=[];
  let nextTimer=0, stopped=0, paused=0, closed=0, state='active', resolveStart;
  const element=()=>({hidden:false,disabled:false,textContent:'',listeners:{},addEventListener(k,fn){this.listeners[k]=fn;},click(){return this.listeners.click?.();}});
  const start=element(),end=element(),status=element(),audio={...element(),pause(){paused++;},play:async()=>{}};
  const host={dataset:{},isConnected:true,querySelector:s=>s.includes('-start')?start:s.includes('-end')?end:s.includes('-status')?status:s.includes('-audio')?audio:null};
  const document={hidden:false,querySelectorAll:s=>s.startsWith('[data-website-receptionist]')?[host]:[],addEventListener(k,f){events.set(k,f);}};
  const stream={getTracks:()=>[{stop(){stopped++;}}]};
  class Peer extends EventTarget {
    constructor(){super();this.iceGatheringState='complete';this.connectionState='new';peers.push(this);}
    addTrack(){} createDataChannel(){return {send(){assert.fail('browser cannot send provider events');}};}
    async createOffer(){return {type:'offer',sdp:'v=0\r\n'};} async setLocalDescription(x){this.localDescription=x;}
    async setRemoteDescription(){this.connectionState='connected';this.ontrack?.({streams:[stream]});}
    close(){closed++;this.connectionState='closed';this.onconnectionstatechange?.();}
  }
  const context={URL,URLSearchParams,AbortController,EventTarget,Promise,RTCPeerConnection:Peer,document,
    location:{origin:'https://www.mylegnd.com',pathname:'/contact',search:'',href:'https://www.mylegnd.com/contact'},
    navigator:{mediaDevices:{getUserMedia:async()=>stream}},
    setTimeout(fn,ms){const id=++nextTimer;timers.set(id,{fn,ms});return id;},clearTimeout:id=>timers.delete(id),
    addEventListener(k,f){events.set(k,f);},
    async fetch(url,options){
      const action=url.split('/').at(-1);requests.push({action,options,body:JSON.parse(options.body)});
      if(action==='status'&&failStatus)throw Error('network unavailable');
      if(action==='start'&&lateStart)await new Promise(resolve=>{resolveStart=resolve;});
      return {ok:true,json:async()=>action==='grant'?{grant:'opaque-grant'}:action==='start'?{handle:'opaque-handle',answer:'v=0\r\n',maximumSeconds:120}:{state:action==='status'?state:'ending'}};
    }};
  context.window=context;vm.runInNewContext(source,context);
  return {start,end,audio,status,requests,peers,document,events,stopped:()=>stopped,paused:()=>paused,closed:()=>closed,
    setState:v=>state=v,resolveStart:()=>resolveStart(),
    async fire(ms){const entry=[...timers].find(([,t])=>t.ms===ms);assert.ok(entry,`missing ${ms}ms timer`);timers.delete(entry[0]);entry[1].fn();await settle();}};
}

test('end immediately stops microphone/playback but retains peer until authenticated server closure',async()=>{
  const f=fixture();await f.start.click();await settle();f.end.click();await settle();
  assert.equal(f.stopped(),1);assert.equal(f.paused(),1);assert.equal(f.closed(),0);assert.equal(f.start.disabled,true);
  assert.equal(f.requests.filter(x=>x.action==='end').length,1);
  f.end.click();f.start.click();assert.equal(f.peers.length,1);
  f.setState('ending');await f.fire(2000);assert.equal(f.closed(),0);
  f.setState('ended');await f.fire(2000);assert.equal(f.closed(),1);assert.equal(f.start.disabled,false);
  assert.equal(f.requests.filter(x=>x.action==='end').length,1);
});

test('silent or unavailable status uses bounded local fallback, not false provider completion',async()=>{
  const f=fixture({failStatus:true});await f.start.click();await settle();
  assert.equal(f.stopped(),1);assert.equal(f.closed(),0);
  await f.fire(15000);assert.equal(f.closed(),1);assert.equal(f.start.disabled,false);
  assert.doesNotMatch(f.status.textContent,/provider.*closed|final usage|charges confirmed/i);
  assert.equal(f.requests.filter(x=>x.action==='start').length,1);
});

test('background stop and pagehide do not duplicate end or restore audio while draining',async()=>{
  const f=fixture();await f.start.click();await settle();f.document.hidden=true;f.events.get('visibilitychange')();f.events.get('pagehide')();await settle();
  assert.equal(f.stopped(),1);assert.equal(f.closed(),0);assert.equal(f.requests.filter(x=>x.action==='end').length,1);
  f.peers[0].ontrack({streams:[{}]});assert.equal(f.audio.srcObject,null);
  await f.fire(15000);assert.equal(f.closed(),1);
});

test('late start response after cancellation ends its original handle once without starting another call',async()=>{
  const f=fixture({lateStart:true});const started=f.start.click();await settle();f.end.click();f.resolveStart();await started;await settle();
  assert.equal(f.stopped(),1);assert.equal(f.closed(),1);
  assert.equal(f.requests.filter(x=>x.action==='start').length,1);assert.equal(f.requests.filter(x=>x.action==='end').length,1);
});
