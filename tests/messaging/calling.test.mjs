import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../SHARED/wwwroot/js/legend-calling.js', import.meta.url), 'utf8');
const deferred = () => { let resolve, reject; const promise = new Promise((r,j) => { resolve=r; reject=j; }); return {promise,resolve,reject}; };
const tick = async () => { for (let i=0;i<12;i++) await Promise.resolve(); };
const track = (kind, id=kind) => ({kind,id,stops:0,stop(){this.stops++;this.readyState='ended';}});
const stream = (...tracks) => ({getTracks:()=>tracks,getAudioTracks:()=>tracks.filter(t=>t.kind==='audio'),getVideoTracks:()=>tracks.filter(t=>t.kind==='video')});
class FakePeer {
  constructor(){this.senders=[];this.transceivers=[];this.connectionState='new';this.calls=[];}
  addTrack(track){const sender=this.sender(track);this.senders.push(sender);return sender;}
  sender(track){return {track,getParameters:()=>({encodings:[{}]}),setParameters:async()=>{},replaceTrack:async function(t){this.track=t;}};}
  addTransceiver(kind){const sender=this.sender(null);this.senders.push(sender);this.transceivers.push({sender,receiver:{track:track(kind,'remote-'+kind)}});}
  getSenders(){return this.senders;} getTransceivers(){return this.transceivers;}
  async createOffer(){this.calls.push('offer');return {type:'offer',sdp:'native-raw-offer'};}
  async createAnswer(){this.calls.push('answer');return {type:'answer',sdp:'native-raw-answer'};}
  async setLocalDescription(value){this.calls.push(['local',value]);}
  async setRemoteDescription(value){this.calls.push(['remote',value]);}
  async addIceCandidate(value){this.calls.push(['candidate',value]);}
  close(){this.closed=true;}
}
const policy={stunUrls:[],connectSeconds:20,recoveryAttempts:2,audioBitrate:64000,videoBitrate:1000000};
function setup(invoke, getUserMedia, getDisplayMedia) {
  const commands=[],shown=[],failures=[],media=[],timers=new Map();let timerId=0;
  const setTimer=(fn,ms)=>{const id=++timerId;timers.set(id,{fn,ms});return id;};
  const context={console, Date, Promise, JSON, Error, Boolean, Math, crypto:{randomUUID:()=> 'new-call'},
    setTimeout:setTimer,clearTimeout(id){timers.delete(id);},setInterval:setTimer,clearInterval(id){timers.delete(id);},
    navigator:{mediaDevices:{getUserMedia,getDisplayMedia}},RTCPeerConnection:FakePeer,
    MediaStream:class {constructor(tracks=[]){this.tracks=[...tracks];}getTracks(){return this.tracks;}addTrack(t){this.tracks.push(t);}}};
  vm.createContext(context);vm.runInContext(source,context);
  const connection={serverTimeoutInMilliseconds:30000,on(){},invoke:async(method, command)=>{commands.push(command); return invoke(command);}};
  const client=new context.LegendBrowserCalling({connection,deviceId:'device',isActor:(id,type)=>id==='self'&&type==='Client',
    present:(call,caller)=>shown.push({call,caller}),media:(...values)=>media.push(values),failure:message=>failures.push(message)});
  return {client,commands,shown,failures,media,timers,context};
}
const snapshot=(extra={})=>({id:'call',conversationId:'conversation',callerUserId:'other',callerType:'Agent',calleeUserId:'self',calleeType:'Client',callerDeviceId:'other-device',calleeDeviceId:null,status:'ringing',epoch:0,expiresUtc:new Date(Date.now()+45000).toISOString(),...extra});
test('incoming receipt follows actual presentation and never grants a different account access',async()=>{
  const c=setup(()=>({succeeded:true}));
  await c.client.receive({call:snapshot({calleeUserId:'someone-else'})});assert.equal(c.shown.length,0);assert.equal(c.commands.length,0);
  await c.client.receive({call:snapshot()});assert.equal(c.shown.length,1);assert.equal(c.commands[0].action,'received');
  await c.client.receive({call:snapshot()});assert.equal(c.commands.length,1);
  c.client.close();
});
test('another device winning answer closes this device without opening media',async()=>{
  const c=setup(()=>({succeeded:true}),()=>{throw Error('media must not open');});
  await c.client.receive({call:snapshot()});
  await c.client.receive({call:snapshot({status:'connecting',calleeDeviceId:'winning-device'})});
  assert.equal(c.client.call,null);assert.equal(c.commands.filter(x=>x.action==='accept').length,0);
});
test('cancel during microphone permission uses stable early-cancel identity and releases late tracks',async()=>{
  const media=deferred();let stopped=0;
  const c=setup(()=>({succeeded:true}),()=>media.promise);
  const start=c.client.start('conversation',false);await c.client.end();
  assert.equal(c.commands[0].action,'cancel');assert.equal(c.commands[0].callId,'new-call');assert.equal(c.commands[0].conversationId,'conversation');
  media.resolve({getTracks:()=>[{stop(){stopped++;}}]});await start;
  assert.equal(stopped,1);assert.equal(c.client.call,null);assert.equal(c.commands.filter(x=>x.action==='invite').length,0);
});
test('permission denial reports failure and never invokes invite',async()=>{
  const c=setup(()=>({succeeded:true}),async()=>{throw Error('Microphone denied');});
  await c.client.start('conversation',true);assert.equal(c.client.call,null);assert.deepEqual(c.failures,['Microphone denied']);
  assert.equal(c.commands.some(x=>x.action==='invite'),false);
});
test('late invite result after cancel cannot resurrect call or leave server invitation ringing',async()=>{
  const invitation=deferred(), invoked=deferred();let stopped=0;
  const c=setup(command=>{if(command.action==='invite'){invoked.resolve();return invitation.promise;}return {succeeded:true};},async()=>({getTracks:()=>[{stop(){stopped++;}}]}));
  const pending=c.client.start('conversation',false);await invoked.promise;
  assert.equal(c.commands[0].action,'invite');await c.client.end();
  invitation.resolve({succeeded:true,call:snapshot({id:'new-call',callerUserId:'self',callerType:'Client',callerDeviceId:'device'})});await pending;
  assert.equal(c.client.call,null);assert.equal(stopped,1);assert.equal(c.commands.at(-1).action,'cancel');
});
test('old call event failure cannot terminate a new current call',async()=>{
  const c=setup(()=>({succeeded:true}));c.client.call=snapshot({id:'new-current'});
  await c.client.fail(Error('old failure'),'old-call');assert.equal(c.client.call.id,'new-current');assert.equal(c.commands.length,0);assert.equal(c.failures.length,0);
  c.client.close();
});
test('double answer opens one microphone and accepts once; close releases every owned track',async()=>{
  const acquired=deferred(), mic=track('audio');let acquisitions=0;
  const c=setup(command=>({succeeded:true,policy,call:snapshot({status:command.action==='accept'?'connecting':'ringing'})}),()=>{acquisitions++;return acquired.promise;});
  await c.client.receive({call:snapshot()});
  const first=c.client.accept(),second=c.client.accept();assert.equal(first,second);assert.equal(acquisitions,1);
  acquired.resolve(stream(mic));await first;
  assert.equal(c.commands.filter(x=>x.action==='accept').length,1);assert.ok(c.client.peer);
  c.client.close();assert.equal(mic.stops,1);
});
test('late accepted response compensates original call and cannot change replacement policy or media',async()=>{
  const accepted=deferred(),sent=deferred(),mic=track('audio');
  const c=setup(command=>{if(command.action==='accept'){sent.resolve();return accepted.promise;}return {succeeded:true};},async()=>stream(mic));
  await c.client.receive({call:snapshot()});const pending=c.client.accept();await sent.promise;
  c.client.close();c.client.call=snapshot({id:'replacement'});c.client.policy={marker:'replacement'};
  accepted.resolve({succeeded:true,call:snapshot({status:'connecting'}),policy});await pending;
  assert.equal(c.client.call.id,'replacement');assert.equal(c.client.policy.marker,'replacement');assert.equal(mic.stops,1);
  assert.equal(c.commands.at(-1).action,'end');assert.equal(c.commands.at(-1).callId,'call');assert.equal(c.failures.length,0);
  c.client.close();
});
test('simultaneous display clicks acquire once and failed replaceTrack immediately releases capture',async()=>{
  const acquired=deferred(),displayTrack=track('video','display');let acquisitions=0;
  const c=setup(()=>({succeeded:true}),null,()=>{acquisitions++;return acquired.promise;});
  c.client.call=snapshot({status:'active'});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  c.client.peer.getSenders().find(s=>!s.track).replaceTrack=async()=>{throw Error('replace denied');};
  const first=c.client.share(),second=c.client.share();assert.equal(first,second);assert.equal(acquisitions,1);
  acquired.resolve(stream(displayTrack));await assert.rejects(first,/replace denied/);assert.equal(displayTrack.stops,1);assert.equal(c.client.display,null);
  c.client.close();
});
test('closing while display permission is pending stops late capture and sends no new-call signals',async()=>{
  const acquired=deferred(),displayTrack=track('video','display');
  const c=setup(()=>({succeeded:true}),null,()=>acquired.promise);
  c.client.call=snapshot({status:'active'});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  const sharing=c.client.share();c.client.close();c.client.call=snapshot({id:'replacement'});
  acquired.resolve(stream(displayTrack));await sharing;
  assert.equal(displayTrack.stops,1);assert.equal(c.commands.length,0);assert.equal(c.client.call.id,'replacement');c.client.close();
});
test('real adapter offer and answer use native raw SDP and buffer matching ICE epoch',async()=>{
  const c=setup(command=>({succeeded:true,policy,call:snapshot({status:'connecting',epoch:command.epoch??1})}));
  c.client.call=snapshot({status:'connecting',epoch:1});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  await c.client.signal({call:c.client.call,signalKind:'candidate',signalData:'{"candidate":"ice-a","sdpMid":"0","sdpMLineIndex":0}'});
  assert.equal(c.client.peer.calls.length,0);
  await c.client.signal({call:c.client.call,signalKind:'offer',signalData:'native-raw-offer'});
  const sent=c.commands.find(x=>x.signalKind==='answer');assert.equal(sent.signalData,'native-raw-answer');assert.equal(sent.epoch,1);
  assert.equal(c.client.peer.calls[0][0],'remote');assert.equal(c.client.peer.calls[1][0],'candidate');
  c.client.close();
  const caller=setup(command=>({succeeded:true,call:snapshot({callerUserId:'self',callerType:'Client',status:'connecting',epoch:command.epoch})}));
  caller.client.call=snapshot({callerUserId:'self',callerType:'Client',status:'connecting'});caller.client.stream=stream(track('audio'));caller.client.policy=policy;await caller.client.ensurePeer();
  await caller.client.offer(false);assert.equal(caller.commands[0].signalData,'native-raw-offer');assert.equal(caller.commands[0].epoch,1);caller.client.close();
});
test('late remote SDP completion cannot answer or mutate a replacement peer',async()=>{
  const remote=deferred();const c=setup(()=>({succeeded:true}));
  c.client.call=snapshot({status:'connecting',epoch:1});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  const oldPeer=c.client.peer;oldPeer.setRemoteDescription=()=>remote.promise;
  const signal=c.client.signal({call:c.client.call,signalKind:'offer',signalData:'old-sdp'});
  c.client.close();c.client.call=snapshot({id:'replacement'});c.client.peer=new FakePeer();c.client.remoteEpoch=7;
  remote.resolve();await signal;assert.equal(c.client.remoteEpoch,7);assert.equal(c.client.peer.calls.length,0);assert.equal(c.commands.length,0);c.client.close();
});
test('late ICE send failure is scoped and cannot drain replacement candidates or end replacement',async()=>{
  const sent=deferred(),invoked=deferred();const c=setup(command=>{if(command.signalKind==='candidate'){invoked.resolve();return sent.promise;}return {succeeded:true};});
  c.client.call=snapshot({status:'connecting',epoch:1});c.client.localReady=true;c.client.localCandidates=[{epoch:1,candidate:{candidate:'old'}}];
  const scope=c.client.scope();const flushing=c.client.flushCandidates().catch(error=>c.client.fail(error,scope));await invoked.promise;
  c.client.close();c.client.call=snapshot({id:'replacement'});c.client.localReady=true;c.client.localCandidates=[{candidate:'new'}];c.client.flushing=true;
  sent.reject(Error('old signal failed'));await flushing;
  assert.equal(c.client.call.id,'replacement');assert.equal(c.client.flushing,true);assert.equal(c.client.localCandidates[0].candidate,'new');assert.equal(c.failures.length,0);assert.equal(c.commands.length,1);c.client.close();
});
test('remote audio and streamless video aggregate in one stream in either event order',async()=>{
  for(const order of [['audio','video'],['video','audio']]) {
    const c=setup(()=>({succeeded:true}));c.client.call=snapshot({status:'active'});c.client.stream=stream(track('audio','local'));c.client.policy=policy;await c.client.ensurePeer();
    const audio=track('audio','remote-audio'),video=track('video','remote-video');
    for(const kind of order)c.client.peer.ontrack({track:kind==='audio'?audio:video,streams:kind==='audio'?[stream(audio)]:[]});
    const remote=c.media.at(-1)[0];c.client.peer.ontrack({track:video,streams:[]});
    assert.equal(c.media.at(-1)[0],remote);assert.equal(remote.getTracks().length,2);assert.ok(remote.getTracks().includes(audio));assert.ok(remote.getTracks().includes(video));c.client.close();
  }
});
test('closed calls and regressing snapshots cannot resurrect ringing or override a newer epoch',async()=>{
  const c=setup(()=>({succeeded:true}));await c.client.receive({call:snapshot()});c.client.close();
  await c.client.receive({call:snapshot()});assert.equal(c.client.call,null);
  c.client.call=snapshot({id:'new',status:'connecting',epoch:2,receivedUtc:'receipt'});
  await c.client.receive({call:snapshot({id:'new',status:'ringing',epoch:2})});assert.equal(c.client.call.status,'connecting');
  assert.equal(c.client.applySnapshot(snapshot({id:'new',status:'active',epoch:1}),c.client.scope()),false);assert.equal(c.client.call.epoch,2);c.client.close();
});
test('late empty sync and heartbeat failures leave replacement untouched',async()=>{
  const response=deferred(),sent=deferred();const c=setup(command=>{sent.resolve(command);return response.promise;});
  const syncing=c.client.sync();await sent.promise;c.client.call=snapshot({id:'replacement'});
  response.resolve({succeeded:true,activeCalls:[]});await syncing;assert.equal(c.client.call.id,'replacement');c.client.close();
  const heartbeat=deferred();const h=setup(()=>heartbeat.promise);h.client.call=snapshot({status:'active'});const scope=h.client.scope();h.client.scheduleHeartbeat(scope);
  const callback=h.timers.get(h.client.heartbeat).fn;const pending=callback();await tick();h.client.close();h.client.call=snapshot({id:'replacement'});
  heartbeat.reject(Error('old heartbeat failed'));await pending;assert.equal(h.client.call.id,'replacement');assert.equal(h.failures.length,0);h.client.close();
});
test('native media-state requests receive current sharing state and preserve remote presentation',async()=>{
  const c=setup(()=>({succeeded:true}));c.client.call=snapshot({status:'active',epoch:3});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  await c.client.signal({call:c.client.call,signalKind:'media-state',signalData:'{"screenSharing":true,"request":true}'});
  assert.equal(c.media.at(-1)[2],true);assert.equal(c.commands.at(-1).signalKind,'media-state');assert.deepEqual(JSON.parse(c.commands.at(-1).signalData),{screenSharing:false});assert.equal(c.commands.at(-1).epoch,3);c.client.close();
});
test('timed-out accept releases media and compensates late server success only for original call',async()=>{
  const accepted=deferred(),sent=deferred(),mic=track('audio');
  const c=setup(command=>{if(command.action==='accept'){sent.resolve();return accepted.promise;}return {succeeded:true};},async()=>stream(mic));
  await c.client.receive({call:snapshot()});const answer=c.client.accept();await sent.promise;
  const timeout=[...c.timers].find(([,timer])=>timer.ms===30000);assert.ok(timeout);c.timers.delete(timeout[0]);timeout[1].fn();await answer;
  assert.equal(c.client.call,null);assert.equal(mic.stops,1);assert.equal(c.failures.length,1);
  c.client.call=snapshot({id:'replacement'});accepted.resolve({succeeded:true,call:snapshot({status:'connecting'}),policy:{bad:'stale'}});await tick();
  assert.equal(c.commands.at(-1).action,'end');assert.equal(c.commands.at(-1).callId,'call');assert.equal(c.client.call.id,'replacement');assert.equal(c.client.policy,null);assert.equal(c.failures.length,1);c.client.close();
});
test('failed cleanup after old call ends never displays failure over replacement call',async()=>{
  const ending=deferred(),sent=deferred();const c=setup(()=>{sent.resolve();return ending.promise;});c.client.call=snapshot({status:'active'});
  const failing=c.client.fail(Error('old media failed'),c.client.scope());await sent.promise;c.client.call=snapshot({id:'replacement'});
  ending.reject(Error('old cleanup failed'));await failing;assert.equal(c.failures.length,0);assert.equal(c.client.call.id,'replacement');c.client.close();
});
test('recovery is bounded by server attempts and no timer continuation can signal a replacement',async()=>{
  const c=setup(()=>({succeeded:true}));c.client.call=snapshot({status:'active'});c.client.policy=policy;c.client.peer=new FakePeer();c.client.peer.connectionState='failed';
  const fire=async id=>{const timer=c.timers.get(id);assert.ok(timer);c.timers.delete(id);await timer.fn();await tick();};
  c.client.recover();await fire(c.client.recovery);await fire(c.client.connectTimer);
  await fire(c.client.recovery);await fire(c.client.connectTimer);
  assert.equal(c.commands.filter(x=>x.signalKind==='restart').length,2);assert.equal(c.client.call,null);assert.equal(c.failures.length,1);
  const h=setup(()=>({succeeded:true}));h.client.call=snapshot({status:'active'});h.client.policy=policy;h.client.peer=new FakePeer();h.client.recover();
  const stale=h.timers.get(h.client.recovery).fn;h.client.close();h.client.call=snapshot({id:'replacement'});await stale();
  assert.equal(h.commands.length,0);assert.equal(h.failures.length,0);h.client.close();
});
test('late local offer completion cannot signal new call and terminal heartbeat closes owned media',async()=>{
  const offer=deferred(),mic=track('audio');const c=setup(()=>({succeeded:true}));
  c.client.call=snapshot({callerUserId:'self',callerType:'Client',status:'connecting'});c.client.policy=policy;c.client.stream=stream(mic);await c.client.ensurePeer();
  const oldPeer=c.client.peer;oldPeer.createOffer=()=>offer.promise;const offering=c.client.offer(false);
  c.client.close();c.client.call=snapshot({id:'replacement'});c.client.peer=new FakePeer();c.client.negotiating=true;
  offer.resolve({type:'offer',sdp:'old-sdp'});await offering;assert.equal(c.commands.length,0);assert.equal(c.client.peer.calls.length,0);assert.equal(c.client.negotiating,true);assert.equal(mic.stops,1);c.client.close();
  const h=setup(()=>({succeeded:true,call:snapshot({status:'ended'})}));h.client.call=snapshot({status:'active'});const hMic=track('audio');h.client.stream=stream(hMic);h.client.scheduleHeartbeat(h.client.scope());
  await h.timers.get(h.client.heartbeat).fn();assert.equal(h.client.call,null);assert.equal(hMic.stops,1);
});
test('new remote offer discards old local ICE before awaiting SDP and duplicate answer is idempotent',async()=>{
  const remote=deferred();const c=setup(()=>({succeeded:true}));c.client.call=snapshot({status:'connecting',epoch:2});c.client.stream=stream(track('audio'));c.client.policy=policy;await c.client.ensurePeer();
  c.client.localReady=true;c.client.localEpoch=1;c.client.localCandidates=[{epoch:1,candidate:{candidate:'old-ice'}}];
  c.client.peer.setRemoteDescription=()=>remote.promise;
  const negotiating=c.client.signal({call:c.client.call,signalKind:'offer',signalData:'new-epoch-sdp'});
  assert.equal(c.client.localReady,false);assert.equal(c.client.localCandidates.length,0);assert.equal(c.client.localEpoch,null);
  remote.resolve();await negotiating;assert.equal(c.commands.filter(x=>x.signalKind==='candidate').length,0);assert.equal(c.commands[0].epoch,2);c.client.close();
  const h=setup(()=>({succeeded:true}));h.client.call=snapshot({callerUserId:'self',callerType:'Client',status:'connecting',epoch:1});h.client.stream=stream(track('audio'));h.client.policy=policy;await h.client.ensurePeer();
  const answer={call:h.client.call,signalKind:'answer',signalData:'same-answer'};await h.client.signal(answer);await h.client.signal(answer);
  assert.equal(h.client.peer.calls.filter(x=>x[0]==='remote').length,1);
  await assert.rejects(h.client.signal({...answer,signalData:'contradictory-answer'}),/conflicting/);h.client.close();
});
test('retirement synchronously stops media and permanently rejects new events, calls and sharing',async()=>{
  let acquired=0;const mic=track('audio');const c=setup(()=>({succeeded:true}),async()=>{acquired++;return stream(track('audio'));},async()=>{acquired++;return stream(track('video'));});
  c.client.call=snapshot({status:'active'});c.client.stream=stream(mic);c.client.policy=policy;await c.client.ensurePeer();const peer=c.client.peer;
  c.client.retire();assert.equal(mic.stops,1);assert.equal(peer.closed,true);assert.equal(c.client.call,null);
  await c.client.receive({call:snapshot({id:'fresh'})});await c.client.accept();await c.client.sync();
  await assert.rejects(c.client.start('conversation',false),/session has ended/);await assert.rejects(c.client.share(),/session has ended/);
  c.client.close();await c.client.receive({call:snapshot({id:'another'})});assert.equal(c.client.call,null);assert.equal(c.commands.length,0);assert.equal(acquired,0);
});
test('retirement during microphone acquisition releases late media without issuing any invitation',async()=>{
  const acquired=deferred(),mic=track('audio');const c=setup(()=>({succeeded:true}),()=>acquired.promise);
  const starting=c.client.start('conversation',false);c.client.retire();acquired.resolve(stream(mic));await starting;
  assert.equal(mic.stops,1);assert.equal(c.commands.length,0);assert.equal(c.failures.length,0);assert.equal(c.client.call,null);
});
test('retirement before a queued command dispatch prevents authenticated invocation',async()=>{
  const c=setup(()=>({succeeded:true}));const pending=c.client.command('sync');c.client.retire();
  await assert.rejects(pending,/session has ended/);assert.equal(c.commands.length,0);
});

const adaptivePolicy = { ...policy, wifiWidth:1280,wifiHeight:720,wifiFps:30,cellularWidth:640,cellularHeight:480,cellularFps:24,
  adaptation:{sampleSeconds:3,recoverySamples:4,lowBandwidth:350000,highBandwidth:900000,highLatencySeconds:.6,lowWidth:320,lowHeight:240,lowFps:12,lowBitrate:180000,mediumWidth:640,mediumHeight:480,mediumFps:18,mediumBitrate:450000,audioPriority:4},
  screenShare:{highWidth:1920,highHeight:1080,highFps:15,highBitrate:2500000,mediumWidth:1280,mediumHeight:720,mediumFps:10,mediumBitrate:1200000,lowWidth:960,lowHeight:540,lowFps:5,lowBitrate:250000,transportHeadroomFraction:.15} };
function qualityFixture() {
  const c=setup(()=>({succeeded:true})), mic=track('audio'), camera=track('video');
  c.client.call=snapshot({status:'active'});c.client.policy=adaptivePolicy;c.client.stream=stream(mic,camera);
  const peer=c.client.peer=new FakePeer();peer.addTrack(mic);peer.addTrack(camera);
  for(const sender of peer.senders) sender.setParameters=async p=>{sender.applied=p;};
  const constraints=[];camera.getSettings=()=>({width:1280,height:720});camera.applyConstraints=async value=>constraints.push(value);
  const report=(bandwidth,latency)=>new Map([['transport',{type:'transport',selectedCandidatePairId:'selected'}],['selected',{availableOutgoingBitrate:bandwidth,currentRoundTripTime:latency}],['unselected',{availableOutgoingBitrate:9999999,currentRoundTripTime:0}]]);
  const sample=async(bandwidth,latency)=>{peer.getStats=async()=>report(bandwidth,latency);c.client.scheduleQualitySample(c.client.scope());const timer=c.timers.get(c.client.qualityTimer);c.timers.delete(c.client.qualityTimer);await timer.fn();};
  return {...c,peer,mic,camera,constraints,sample,report};
}
test('shared policy lowers video immediately for selected transport latency and recovers only after valid consecutive samples',async()=>{
  const c=qualityFixture();await c.sample(2000000,1);assert.equal(c.client.quality,0);
  assert.equal(c.peer.senders[1].applied.encodings[0].maxBitrate,180000);
  assert.equal(c.peer.senders[0].applied.encodings[0].priority,'high');
  assert.equal(c.constraints.at(-1).frameRate.max,12);
  await c.sample(2000000,.1);await c.sample(undefined,undefined);assert.equal(c.client.healthySamples,0);
  for(let i=0;i<3;i++)await c.sample(2000000,.1);assert.equal(c.client.quality,0);
  await c.sample(2000000,.1);assert.equal(c.client.quality,1);
  c.client.close();
});
test('missing, negative or nonfinite bandwidth never proves recovery',async()=>{
  const c=qualityFixture();c.client.quality=0;
  for(const value of [undefined,-1,NaN,Infinity]){await c.sample(value,.1);assert.equal(c.client.quality,0);assert.equal(c.client.healthySamples,0);}
  c.client.close();
});
test('screen policy reserves audio and transport, pauses zero-budget video, and restores camera limits',async()=>{
  const c=qualityFixture(); const display=track('video','screen'), constraints=[];
  display.getSettings=()=>({width:1080,height:1920});display.applyConstraints=async value=>constraints.push(value);
  c.client.display=stream(display);c.peer.senders[1].track=display;
  await c.sample(50000,.2);
  assert.equal(c.peer.senders[1].applied.encodings[0].active,false);
  assert.equal(c.peer.senders[0].applied.encodings[0].active,true);
  assert.equal(c.peer.senders[0].applied.encodings[0].maxBitrate,64000);
  assert.equal(constraints.at(-1).width.ideal,540);assert.equal(constraints.at(-1).height.ideal,960);assert.equal(constraints.at(-1).frameRate.max,5);
  await c.sample(300000,.2);assert.equal(c.peer.senders[1].applied.encodings[0].maxBitrate,191000);
  await c.client.stopSharing();assert.equal(c.peer.senders[1].track,c.camera);assert.equal(c.peer.senders[1].applied.degradationPreference,'balanced');assert.equal(c.peer.senders[1].applied.encodings[0].maxBitrate,180000);
  c.client.close();
});
test('late quality report cannot adapt the replacement call',async()=>{
  const c=qualityFixture(), stats=deferred();c.peer.getStats=()=>stats.promise;
  c.client.scheduleQualitySample(c.client.scope());const timer=c.timers.get(c.client.qualityTimer);const pending=timer.fn();
  c.client.close();c.client.call=snapshot({id:'next'});c.client.policy=adaptivePolicy;
  stats.resolve(c.report(0,1));await pending;
  assert.equal(c.client.quality,2);assert.equal(c.constraints.length,0);assert.equal(c.client.call.id,'next');c.client.close();
});
test('late capture-constraint completion cannot apply old sender settings to replacement media',async()=>{
  const c=qualityFixture(), applied=deferred();c.camera.applyConstraints=()=>applied.promise;
  const pending=c.client.applyMediaPolicy();c.client.close();c.client.call=snapshot({id:'next'});c.client.policy=adaptivePolicy;
  const replacement=c.client.peer=new FakePeer();replacement.addTrack(track('audio','next-mic'));
  applied.resolve();await pending;assert.equal(replacement.senders[0].applied,undefined);assert.equal(c.client.call.id,'next');c.client.close();
});

test('late old ICE generation candidates cannot be relabeled with the new epoch', async()=>{
  const c=setup(()=>({succeeded:true}));c.client.call=snapshot({status:'active',epoch:2});c.client.policy=policy;c.client.stream=stream(track('audio'));
  await c.client.ensurePeer();c.client.localEpoch=2;c.client.localReady=true;c.client.localIceUfrags=new Set(['current-generation']);
  const emit=ufrag=>c.client.peer.onicecandidate({candidate:{usernameFragment:ufrag,toJSON:()=>({candidate:'candidate-'+ufrag,usernameFragment:ufrag})}});
  emit('old-generation');emit(undefined);emit('current-generation');await tick();
  const candidates=c.commands.filter(command=>command.signalKind==='candidate');
  assert.equal(candidates.length,1);assert.equal(candidates[0].epoch,2);assert.equal(JSON.parse(candidates[0].signalData).usernameFragment,'current-generation');c.client.close();
});

test('failed quality sampling breaks the consecutive healthy recovery sequence', async()=>{
  const c=qualityFixture();c.client.quality=0;
  await c.sample(2000000,.1);await c.sample(2000000,.1);await c.sample(2000000,.1);
  c.peer.getStats=async()=>{throw Error('stats unavailable');};c.client.scheduleQualitySample(c.client.scope());await c.timers.get(c.client.qualityTimer).fn();
  assert.equal(c.client.healthySamples,0);await c.sample(2000000,.1);assert.equal(c.client.quality,0);c.client.close();
});
test('screen capture constraints enforce server ceilings and preserve a tall aspect ratio', async()=>{
  const c=qualityFixture(), display=track('video','tall-screen'), applied=[];
  display.getSettings=()=>({width:1000,height:2400});display.applyConstraints=async value=>applied.push(value);
  c.client.display=stream(display);c.peer.senders[1].track=display;
  await c.client.applyMediaPolicy();const limits=applied.at(-1);
  assert.equal(limits.width.max,1080);assert.equal(limits.height.max,1920);
  assert.equal(limits.width.ideal,800);assert.equal(limits.height.ideal,1920);assert.equal(limits.frameRate.max,15);c.client.close();
});

test('missing quality observations cannot release the last measured audio reservation', async()=>{
  const c=qualityFixture(), display=track('video','screen');c.client.display=stream(display);c.peer.senders[1].track=display;
  await c.sample(50000,.1);assert.equal(c.peer.senders[1].applied.encodings[0].active,false);
  for(const missing of [undefined,NaN,-1]) { await c.sample(missing,.1);assert.equal(c.peer.senders[1].applied.encodings[0].active,false);assert.equal(c.client.availableBandwidth,50000); }
  await c.sample(300000,.1);assert.equal(c.peer.senders[1].applied.encodings[0].active,true);c.client.close();
});

test('server cellular ceilings constrain every camera tier even when tighter than adaptation defaults', async()=>{
  const c=qualityFixture();c.context.navigator.connection={type:'cellular'};
  c.client.policy={...adaptivePolicy,cellularWidth:240,cellularHeight:180,cellularFps:8};
  for(const tier of [0,1,2]){c.client.quality=tier;const limits=c.client.mediaLimits();assert.equal(limits.width,240);assert.equal(limits.height,180);assert.equal(limits.fps,8);}
  c.client.close();
});
