import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../SHARED/wwwroot/js/messaging.js', import.meta.url), 'utf8');
function implementation(name) {
  const start = source.search(new RegExp(`^  (?:async )?function ${name}\\(`, 'm'));
  assert.notEqual(start, -1);
  const tail = source.slice(start + 1);
  const next = tail.search(/\n  (?:async )?function /);
  return next < 0 ? tail : tail.slice(0, next);
}
const deferred = () => { let resolve, reject; const promise = new Promise((a,b) => { resolve=a;reject=b; }); return {promise,resolve,reject}; };
function environment(request) {
  const context = {
    state: { active:null, requestedConversationId:null, navigationVersion:0, detailFlights:new Map(), readFlights:new Map(), readAcknowledged:new Map(), scrollPositions:{}, inboxDirty:false, inboxFlight:null },
    elements:{newMessages:{hidden:false},messages:{scrollTop:0}}, request,
    writeSession(){},renderConversation(){},renderConversations(){},renderSearchResults(){},setUnreadCount(){},showError(){},
    isCurrentParticipant:(id,type)=>id==='self'&&type==='Client',parseUtcTimestamp:value=>value?new Date(value):null
  };
  vm.createContext(context);
  for (const name of ['latestReadMessageIndex','refreshList','acknowledgeVisibleConversation','loadConversation']) vm.runInContext(implementation(name), context);
  return context;
}
test('latest read boundary ignores self and distinguishes newer sent messages', () => {
  const c=environment(()=>{});
  const messages=[1,2,3].map(i=>({id:String(i),senderUserId:'self',senderType:'Client',sentUtc:`2026-09-11T00:00:0${i}Z`}));
  const conversation={readReceipts:{readers:[{userId:'other',participantType:'Agent',readThroughUtc:messages[1].sentUtc}]}};
  assert.equal(c.latestReadMessageIndex(conversation,messages),1);
  assert.equal(c.latestReadMessageIndex({readReceipts:{readers:[{userId:'self',participantType:'Client',readThroughUtc:messages[2].sentUtc}]}},messages),-1);
  assert.equal(c.latestReadMessageIndex({},messages),-1);
});
test('late A cannot replace selected B; refresh preserves uncertain send identity',async()=>{
  const a=deferred(),b=deferred();
  const c=environment(url=>url.includes('/A?')?a.promise:b.promise);
  const pending={clientMessageId:'retry-stable'};c.state.pendingSubmission=pending;
  const first=c.loadConversation('A',false),second=c.loadConversation('B',false);
  b.resolve({conversation:{id:'B',messages:[]}});await second;
  a.resolve({conversation:{id:'A',messages:[]}});await first;
  assert.equal(c.state.active.id,'B');assert.equal(c.state.pendingSubmission,pending);
});
test('simultaneous opens share one detail and one read acknowledgement',async()=>{
  const detail=deferred(),read=deferred();let reads=0,details=0;
  const c=environment(url=>{if(url.endsWith('/Read')){reads++;return read.promise;}details++;return detail.promise;});
  const first=c.loadConversation('A',true),second=c.loadConversation('A',true);
  detail.resolve({conversation:{id:'A',messages:[{id:'m1'}]}});
  await new Promise(resolve=>setImmediate(resolve));assert.equal(details,1);assert.equal(reads,1);
  read.resolve({});await Promise.all([first,second]);assert.equal(reads,1);
});
test('read failure stays retryable',async()=>{
  let calls=0;const c=environment(async()=>{if(++calls===1)throw Error('offline');return {};});
  const conversation={id:'A',messages:[{id:'m1'}]};
  await assert.rejects(c.acknowledgeVisibleConversation(conversation));
  await c.acknowledgeVisibleConversation(conversation);assert.equal(calls,2);
});
test('inbox burst shares in-flight work and retains one trailing invalidation',async()=>{
  const first=deferred();let calls=0;
  const c=environment(()=>{calls++;return calls===1?first.promise:Promise.resolve({conversations:[{id:'fresh'}]});});
  const a=c.refreshList(),b=c.refreshList(),d=c.refreshList();first.resolve({conversations:[]});await Promise.all([a,b,d]);
  assert.equal(calls,2);assert.equal(c.state.conversations[0].id,'fresh');
});

class Element {
  constructor(tag) { this.tagName=tag;this.children=[];this.events={};this.attributes={}; }
  append(...children) { this.children.push(...children); }
  setAttribute(key,value) { this.attributes[key]=value; }
  addEventListener(name,handler) { this.events[name]=handler; }
  focus() { this.focused=true; }
}
function domEnvironment(request) {
  const c=environment(request);
  c.document={createElement:tag=>new Element(tag)};
  c.createTextElement=(tag,cls,text)=>{ const element=new Element(tag);element.className=cls;element.textContent=text;return element; };
  c.window={getSelection:()=>({toString:()=>''}),setTimeout,clearTimeout};
  for(const name of ['setMessageReaction','appendMessageInteractions','appendSharedContent']) vm.runInContext(implementation(name),c);
  return c;
}
test('captionless shared content carries actual media and canonical protected link',()=>{
  const c=domEnvironment(()=>{});const card=new Element('article');
  c.appendSharedContent(card,{sourcePostId:'post-id',status:'available',media:[{id:'asset-id',mediaKind:'Image',displayOrder:0}],contentType:'Story'});
  const children=card.children[0].children;
  assert.equal(children.find(x=>x.tagName==='img').src,'/Social/Media/asset-id');
  assert.equal(children.find(x=>x.tagName==='a').href,'/Social/Posts/post-id');
  const unavailable=new Element('article');c.appendSharedContent(unavailable,{sourcePostId:'post-id',status:'unavailable',media:[{id:'secret',mediaKind:'Image'}]});
  assert.equal(unavailable.children[0].children.filter(x=>x.tagName==='img'||x.tagName==='a').length,0);
});
test('reaction controls consume server palette and double tap explicitly sets like',async()=>{
  const calls=[];const c=domEnvironment(async(url,options)=>{calls.push({url,options});return {messageId:'m',reactions:[{emoji:'👍',count:1,reactedByCurrentActor:true}]};});
  c.state.active={id:'A',messages:[{id:'m'}]};
  const card=new Element('article');c.appendMessageInteractions(card,{id:'A',reactionOptions:['🦉']},{id:'m',reactions:[]});
  const menu=card.children[0].children[0],palette=menu.children[1];
  assert.equal(palette.children[0].textContent,'🦉');
  card.events.dblclick({target:{closest:()=>false}});
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls.length,1);assert.equal(calls[0].options.method,'PUT');assert.equal(JSON.parse(calls[0].options.body).emoji,'👍');
  assert.equal(c.state.active.messages[0].reactions[0].count,1);
});
