import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {JSDOM} from 'jsdom';
import {publicInquiryForm} from '../../SHARED/WebsitePlatform/public-inquiry-form.mjs';

const source = readFileSync(new URL('../../Legend-Design/legend-public-inquiry.js', import.meta.url), 'utf8');
const settle = async () => { for (let i=0;i<5;i++) await new Promise(resolve=>setTimeout(resolve,0)); };
function fixture({grantAvailable=true, microphone, uncertainStart=false, delayedIce=false, connects=true, restricted=false, foreignTestRoute=false}={}) {
  const dom = new JSDOM(publicInquiryForm(), {url:'https://www.mylegnd.com/contact',runScripts:'outside-only'});
  const w=dom.window, calls=[], peers=[], channels=[];
  if (restricted) {
    const host=w.document.querySelector('[data-website-receptionist]');
    host.dataset.receptionistTestGrant=foreignTestRoute?'https://foreign.example.test/grant':'/settings/test/grant';
    host.dataset.receptionistTestStart='/settings/test/start';
    const token=w.document.createElement('input');token.name='__RequestVerificationToken';token.value='synthetic-antiforgery';host.append(token);
  }
  let startupTimer, closeTimer, pollTimer, providerState = "active";
  const nativeSetTimeout=w.setTimeout.bind(w), nativeClearTimeout=w.clearTimeout.bind(w);
  w.setTimeout=(callback,ms,...args)=>{
    const id=nativeSetTimeout(callback,ms,...args);
    if(ms===45000)startupTimer={id,callback,cleared:false};
    if(ms===15000)closeTimer={id,callback};
    if(ms===2000)pollTimer={id,callback};
    return id;
  };
  w.clearTimeout=id=>{if(startupTimer?.id===id)startupTimer.cleared=true;nativeClearTimeout(id);};
  let microphoneRequests=0, stopped=0, closed=0;
  const stream={getTracks:()=>[{stop:()=>stopped++}]};
  Object.defineProperty(w.navigator,'mediaDevices',{value:{getUserMedia:async()=>{
    microphoneRequests++;return microphone ? microphone(stream) : stream;
  }}});
  w.HTMLMediaElement.prototype.play=async()=>{};
  w.HTMLMediaElement.prototype.pause=()=>{};
  w.RTCPeerConnection=class extends w.EventTarget {
    constructor(){super();this.connectionState='new';this.iceGatheringState=delayedIce?'gathering':'complete';peers.push(this);}
    addTrack(){}
    createDataChannel(label){const channel={label,send(){throw new Error('Browser commands are forbidden');}};channels.push(channel);return channel;}
    finishIce(){this.iceGatheringState='complete';this.localDescription.sdp+='-with-candidate';this.dispatchEvent(new w.Event('icegatheringstatechange'));}
    async createOffer(){return {type:'offer',sdp:'v=0\r\nfixture-offer'};}
    async setLocalDescription(value){this.localDescription=value;}
    async setRemoteDescription(){this.connectionState=connects?'connected':'connecting';if(connects)this.ontrack?.({streams:[stream]});this.onconnectionstatechange?.();}
    close(){closed++;this.connectionState='closed';this.onconnectionstatechange?.();}
  };
  w.fetch=async(url,options)=>{
    const action=url.split('/').at(-1), body=JSON.parse(options.body);
    calls.push({action,body,options});
    assert.equal(new URL(url,w.location.href).origin,restricted?w.location.origin:'https://portal.mylegnd.com');
    assert.equal(options.credentials,restricted?'same-origin':'omit');
    if(restricted)assert.equal(options.headers.RequestVerificationToken,'synthetic-antiforgery');
    else assert.equal(options.headers.RequestVerificationToken,undefined);
    if(action==='grant')return {ok:grantAvailable,json:async()=>({grant:'opaque-grant'})};
    if(action==='start'){
      if(uncertainStart)throw new Error('unknown provider outcome');
      return {ok:true,json:async()=>({answer:'v=0\r\nfixture-answer',handle:'opaque-handle',maximumSeconds:300})};
    }
    return {ok:true,json:async()=>({state:action==='status'?providerState:'ending'})};
  };
  w.eval(source);
  const start=w.document.querySelector('[data-receptionist-start]'), end=w.document.querySelector('[data-receptionist-end]');
  return {w,calls,start,end,peers,channels,startup:()=>startupTimer,status:()=>w.document.querySelector('[data-receptionist-status]').textContent,
    microphoneRequests:()=>microphoneRequests,stopped:()=>stopped,closed:()=>closed,
    providerEnded(){providerState='ended';nativeClearTimeout(pollTimer.id);pollTimer.callback();},
    closeFallback(){nativeClearTimeout(closeTimer.id);closeTimer.callback();},
    close(){end.click();w.close();}};
}

test('website voice starts once with media only and preserves the contact fallback',async()=>{
  const f=fixture();try{
    assert.equal(f.microphoneRequests(),0);
    assert.ok(f.w.document.querySelector('[data-website-inquiry]'));
    f.start.click();f.start.click();await settle();
    assert.deepEqual(f.calls.filter(x=>x.action==='grant').map(x=>x.body),[{sourcePath:'/contact'}]);
    assert.deepEqual(f.calls.filter(x=>x.action==='start').map(x=>x.body),[{grant:'opaque-grant',offer:'v=0\r\nfixture-offer'}]);
    assert.equal(f.microphoneRequests(),1);assert.equal(f.peers.length,1);
    assert.equal(f.channels[0].label,'oai-events');assert.equal(f.startup().cleared,true);
    assert.match(f.status(),/Connected to the AI receptionist/);
    f.end.click();await settle();
    assert.equal(f.stopped(),1);assert.equal(f.closed(),0);
    assert.equal(f.calls.filter(x=>x.action==='end').length,1);
    assert.equal(f.start.disabled,true);
    f.providerEnded();await settle();
    assert.equal(f.closed(),1);assert.equal(f.start.disabled,false);
  }finally{f.close();}
});

test('unavailable admission never requests microphone or creates a provider session',async()=>{
  const f=fixture({grantAvailable:false});try{
    f.start.click();await settle();
    assert.equal(f.microphoneRequests(),0);assert.equal(f.calls.length,1);
    assert.match(f.status(),/send an inquiry on this page/);
    assert.equal(f.w.document.querySelector('form [type=submit]').disabled,false);
  }finally{f.close();}
});

test('microphone refusal leaves contact usable without a start attempt',async()=>{
  const f=fixture({microphone:async()=>{throw new Error('denied');}});try{
    f.start.click();await settle();
    assert.equal(f.calls.filter(x=>x.action==='start').length,0);
    assert.equal(f.start.disabled,false);assert.match(f.status(),/microphone access was declined/);
  }finally{f.close();}
});

test('uncertain creation is not retried and local microphone is released',async()=>{
  const f=fixture({uncertainStart:true});try{
    f.start.click();await settle();
    assert.equal(f.calls.filter(x=>x.action==='start').length,1);
    assert.equal(f.calls.filter(x=>x.action==='end').length,0);
    assert.equal(f.stopped(),1);assert.equal(f.closed(),1);
    assert.match(f.status(),/send an inquiry on this page/);
  }finally{f.close();}
});

test('ending during the microphone prompt releases a late stream without creating a call',async()=>{
  let approve;
  const f=fixture({microphone:stream=>new Promise(resolve=>{approve=()=>resolve(stream);})});try{
    f.start.click();await settle();f.end.click();approve();await settle();
    assert.equal(f.stopped(),1);assert.equal(f.calls.filter(x=>x.action==='start').length,0);
  }finally{f.close();}
});

test('backgrounding ends media and sends only the scoped end handle',async()=>{
  const f=fixture();try{
    f.start.click();await settle();
    Object.defineProperty(f.w.document,'hidden',{value:true,configurable:true});
    f.w.document.dispatchEvent(new f.w.Event('visibilitychange'));await settle();
    assert.deepEqual(f.calls.filter(x=>x.action==='end').map(x=>x.body),[{handle:'opaque-handle'}]);
    assert.equal(f.stopped(),1);assert.match(f.status(),/background/);
  }finally{f.close();}
});


test('SDP exchange waits for complete ICE candidates',async()=>{
  const f=fixture({delayedIce:true});try{
    f.start.click();await settle();
    assert.equal(f.calls.filter(x=>x.action==='start').length,0);
    f.peers[0].finishIce();await settle();
    assert.equal(f.calls.find(x=>x.action==='start').body.offer,'v=0\r\nfixture-offer-with-candidate');
  }finally{f.close();}
});

test('a peer that never connects retains startup deadline and releases its original handle',async()=>{
  const f=fixture({connects:false});try{
    f.start.click();await settle();
    assert.equal(f.startup().cleared,false);
    f.startup().callback();await settle();
    assert.equal(f.stopped(),1);assert.equal(f.closed(),0);
    f.closeFallback();assert.equal(f.closed(),1);
    assert.equal(f.calls.filter(x=>x.action==='start').length,1);
    assert.equal(f.calls.filter(x=>x.action==='end').length,1);
    assert.match(f.status(),/could not connect/);
  }finally{f.close();}
});

test('restricted management voice reuses media flow with same-origin authentication and antiforgery',async()=>{
  const f=fixture({restricted:true});try{
    f.start.click();await settle();assert.equal(f.microphoneRequests(),1);
    assert.equal(f.calls.filter(x=>x.action==='start').length,1);
    f.end.click();await settle();assert.equal(f.calls.filter(x=>x.action==='end').length,1);
  }finally{f.close();}
});

test('restricted route cannot send cookies or antiforgery to another origin',async()=>{
  const f=fixture({restricted:true,foreignTestRoute:true});try{
    f.start.click();await settle();assert.equal(f.calls.length,0);assert.equal(f.microphoneRequests(),0);
  }finally{f.close();}
});

test('historical classic handler owns a late canonical module mount and stops its one active call when removed',async()=>{
  const f=fixture();
  try{
    f.w.document.querySelector('[data-website-receptionist]').remove();
    const classic=f.w.document.createElement('script');classic.src='/legend-public-inquiry.js?v=historical';f.w.document.head.append(classic);
    const form=f.w.document.querySelector('[data-website-inquiry]');
    const message=form.querySelector('[name="Message"]');message.value='Preserve my written question';
    const host=f.w.document.createElement('div');host.dataset.legendReceptionistEntry='';host.dataset.receptionistLabel='Speak with us';form.prepend(host);
    const moduleSource=readFileSync(new URL('../../SHARED/WebsitePlatform/public-inquiry-form.mjs',import.meta.url),'utf8');
    // Execute the shipped module body in the existing JSDOM realm, changing only
    // ESM syntax boundaries. A second dynamic import is not supplied or mocked.
    f.w.eval(moduleSource.replace(/^export /gm,'').replaceAll('import.meta.url',JSON.stringify('https://www.mylegnd.com/public-inquiry-form.mjs')));
    f.w.document.dispatchEvent(new f.w.Event('DOMContentLoaded'));await settle();
    assert.equal(f.calls.length,0);assert.equal(f.microphoneRequests(),0);
    const start=host.querySelector('[data-receptionist-start]');
    assert.equal(start.textContent,'Speak with us');
    start.click();start.click();await settle();
    assert.equal(f.calls.filter(call=>call.action==='grant').length,1);
    assert.equal(f.calls.filter(call=>call.action==='start').length,1);
    assert.equal(f.microphoneRequests(),1);
    f.w.dispatchEvent(new f.w.Event('legend:website-content-rendered'));await settle();
    assert.equal(form.querySelectorAll('[data-website-receptionist]').length,1);
    assert.equal(form.querySelector('[name="Message"]'),message);assert.equal(message.value,'Preserve my written question');
    host.remove();f.w.dispatchEvent(new f.w.Event('legend:website-content-rendered'));await settle();
    assert.equal(f.calls.filter(call=>call.action==='end').length,1);assert.equal(f.stopped(),1);
    f.closeFallback();assert.equal(f.closed(),1);
  }finally{f.close();}
});
