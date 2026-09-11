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
    state: { active:null, requestedConversationId:null, navigationVersion:0, detailFlights:new Map(), detailRevisions:new Map(), readFlights:new Map(), reactionFlights:new Map(), isOpen:true, readAcknowledged:new Map(), scrollPositions:{}, inboxDirty:false, inboxFlight:null },
    document:{hidden:false},
    elements:{newMessages:{hidden:false},messages:{scrollTop:0}}, request,
    writeSession(){},renderConversation(){},renderConversations(){},renderSearchResults(){},setUnreadCount(){},showError(){},
    isCurrentParticipant:(id,type)=>id==='self'&&type==='Client',parseUtcTimestamp:value=>value?new Date(value):null
  };
  vm.createContext(context);
  for (const name of ['selectDraftRecipient','latestReadMessageIndex','refreshList','acknowledgeVisibleConversation','loadConversation']) vm.runInContext(implementation(name), context);
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
  const c=environment(url=>{if(url.includes('/Read?')){reads++;return read.promise;}details++;return detail.promise;});
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
test('a held thread cannot replace a newly selected recipient draft',async()=>{
  const old=deferred(),c=environment(()=>old.promise);
  const opening=c.loadConversation('A',false);c.selectDraftRecipient({userId:'B'});
  old.resolve({conversation:{id:'A',messages:[]}});await opening;
  assert.equal(c.state.active,null);assert.equal(c.state.draftTarget.userId,'B');
});
test('realtime invalidation during a snapshot queues a fresh coalesced read',async()=>{
  const old=deferred();let calls=0;const c=environment(()=>++calls===1?old.promise:Promise.resolve({conversation:{id:'A',messages:[{id:'new'}]}}));
  const opening=c.loadConversation('A',false);
  const first=c.loadConversation('A',false,false,true),second=c.loadConversation('A',false,false,true);
  old.resolve({conversation:{id:'A',messages:[]}});await Promise.all([opening,first,second]);
  assert.equal(calls,2);assert.equal(c.state.active.messages[0].id,'new');
});

const flushTasks = () => new Promise(resolve => setImmediate(resolve));

test('reaction mutations serialize per message and keep the latest explicit selection', async () => {
  const first = deferred(), second = deferred(), calls = [];
  const c = domEnvironment((url, options) => {
    calls.push({ url, options });
    return calls.length === 1 ? first.promise : second.promise;
  });
  const message = { id: 'message', reactions: [] };
  c.state.active = { id: 'A', messages: [message] };
  const like = c.setMessageReaction('A', message, '👍');
  const heart = c.setMessageReaction('A', message, '❤️');
  await flushTasks();
  assert.equal(calls.length, 1, 'The newer selection must wait for the first mutation');
  assert.equal(JSON.parse(calls[0].options.body).emoji, '👍');
  first.resolve({ messageId: 'message', reactions: [{ emoji: '👍', count: 2, reactedByCurrentActor: true }] });
  await like;
  await flushTasks();
  assert.equal(calls.length, 2);
  assert.equal(JSON.parse(calls[1].options.body).emoji, '❤️');
  assert.equal(c.state.active.messages[0].reactions[0].emoji, '👍');
  second.resolve({ messageId: 'message', reactions: [{ emoji: '❤️', count: 3, reactedByCurrentActor: true }] });
  await heart;
  assert.equal(c.state.active.messages[0].reactions[0].emoji, '❤️');
  assert.equal(c.state.active.messages[0].reactions[0].count, 3);
  assert.equal(c.state.reactionFlights.size, 0);
});

test('queued reaction survives prior failure without leaking an old thread error', async () => {
  const first = deferred(), calls = [], errors = [];
  const c = domEnvironment((url, options) => {
    calls.push({ url, options });
    return calls.length === 1 ? first.promise : Promise.resolve({ messageId: 'message', reactions: [] });
  });
  c.showError = error => errors.push(error);
  const message = { id: 'message', reactions: [] };
  c.state.active = { id: 'A', messages: [message] };
  const like = c.setMessageReaction('A', message, '👍');
  const remove = c.setMessageReaction('A', message, null);
  await flushTasks();
  c.selectDraftRecipient({ userId: 'B', participantType: 'Client' });
  first.reject(new Error('Old thread unavailable'));
  await Promise.all([like, remove]);
  assert.equal(calls.length, 2);
  assert.equal(calls[1].options.method, 'DELETE');
  assert.equal(errors.length, 0);
  assert.equal(c.state.active, null);
  assert.equal(c.state.draftTarget.userId, 'B');
});

for (const hiddenBy of ['closing the command center', 'hiding the document']) {
  test(`${hiddenBy} during detail loading prevents a read acknowledgement`, async () => {
    const detail = deferred(), calls = [];
    const c = environment(url => {
      calls.push(url);
      assert.equal(url.includes('/Read?'), false, 'A hidden detail must not acknowledge a rendered boundary');
      return detail.promise;
    });
    const opening = c.loadConversation('A', true);
    if (hiddenBy === 'closing the command center') c.state.isOpen = false;
    else c.document.hidden = true;
    detail.resolve({ conversation: { id: 'A', messages: [{ id: 'not-viewed' }] } });
    await opening;
    assert.equal(calls.length, 1);
    assert.equal(c.state.readAcknowledged.size, 0);
  });
}

function submissionEnvironment(request) {
  const c = environment(request);
  Object.assign(c.state, { pendingSubmissions: new Map(), pendingSubmission: null, drafts: {}, draftTarget: null });
  Object.assign(c.elements, { messageBody: { value: 'Submitted body' }, files: { files: [], value: '' }, sendButton: { disabled: false } });
  c.FormData = FormData;
  c.token = null;
  c.renderSelectedFiles = () => {};
  c.participantIdentityKey = (id, type) => `${type}:${id}`;
  let identity = 0;
  c.clientMessageId = () => `test-submission-${++identity}`;
  for (const name of ['activeDraftKey', 'saveDraft', 'createSubmission', 'uploadAttachments', 'sendMessage'])
    vm.runInContext(implementation(name), c);
  return c;
}

test('double form submission shares the message and every attachment upload', async () => {
  const acknowledgement = deferred(), upload = deferred(), calls = [];
  const c = submissionEnvironment((url, options) => {
    calls.push({ url, options });
    if (url === '/Messaging/Conversations/A/Messages') return acknowledgement.promise;
    if (url.endsWith('/Attachments')) return upload.promise;
    if (url === '/Messaging/Conversations') return Promise.resolve({ conversations: [] });
    return Promise.resolve({ conversation: { id: 'A', messages: [{ id: 'committed' }] } });
  });
  c.state.active = { id: 'A', messages: [] };
  c.elements.files.files = [new Blob(['original attachment'], { type: 'text/plain' })];
  const sending = c.sendMessage();
  const submission = c.state.pendingSubmissions.get('conversation:A');
  await c.sendMessage();
  assert.equal(calls.filter(call => call.url.endsWith('/Messages')).length, 1);
  acknowledgement.resolve({ message: { id: 'committed' } });
  await flushTasks();
  assert.equal(calls.filter(call => call.url.endsWith('/Attachments')).length, 1);
  await c.sendMessage();
  assert.equal(calls.filter(call => call.url.endsWith('/Attachments')).length, 1, 'An in-flight upload must not be sent again');
  upload.resolve({});
  await sending;
  await flushTasks();
  assert.equal(calls.filter(call => call.url.endsWith('/Messages')).length, 1);
  assert.equal(calls.filter(call => call.url.endsWith('/Attachments')).length, 1);
  assert.deepEqual(Array.from(submission.uploadedFileIndexes), [0]);
  assert.equal(submission.sending, false);
  assert.equal(c.state.pendingSubmissions.size, 0);
});

test('create acknowledgement after navigation retains canonical attachment retry ownership', async () => {
  const acknowledgement = deferred(), upload = deferred(), calls = [];
  const errors = [];
  const c = submissionEnvironment((url, options) => {
    calls.push({ url, options });
    if (url === '/Messaging/Conversations' && options?.method === 'POST') return acknowledgement.promise;
    if (url.endsWith('/Attachments')) return upload.promise;
    throw new Error(`Unexpected request: ${url}`);
  });
  c.showError = error => errors.push(error);
  const originalTarget = { userId: 'A', participantType: 'Client', contactKey: 'typed-A' };
  c.selectDraftRecipient(originalTarget);
  const originalFile = new Blob(['owned attachment'], { type: 'text/plain' });
  c.elements.files.files = [originalFile];
  c.saveDraft();
  const sending = c.sendMessage();
  const originalKey = 'recipient:Client:A';
  const submission = c.state.pendingSubmissions.get(originalKey);
  c.selectDraftRecipient({ userId: 'B', participantType: 'Agent', contactKey: 'typed-B' });
  c.elements.messageBody.value = 'New recipient draft';
  const newFile = new Blob(['new attachment'], { type: 'text/plain' });
  c.elements.files.files = [newFile];
  c.saveDraft();
  acknowledgement.resolve({ conversation: { id: 'created-A', messages: [
    { id: 'committed-A', senderUserId: 'self', senderType: 'Client', body: 'Submitted body' }
  ] } });
  await flushTasks();
  assert.equal(c.state.pendingSubmissions.get('conversation:created-A'), submission);
  assert.equal(c.state.pendingSubmissions.get(originalKey), submission);
  assert.equal(submission.messageId, 'committed-A');
  assert.equal(submission.files[0], originalFile);
  assert.equal(calls.filter(call => call.url.endsWith('/Attachments')).length, 1);
  upload.reject(new Error('Attachment delivery failed'));
  await sending;
  assert.equal(c.state.pendingSubmissions.get('conversation:created-A'), submission, 'Known commit must remain retryable by conversation ID');
  assert.equal(c.state.active, null);
  assert.equal(c.state.draftTarget.userId, 'B');
  assert.equal(c.elements.messageBody.value, 'New recipient draft');
  assert.equal(c.elements.files.files[0], newFile);
  assert.equal(c.state.drafts['recipient:Agent:B'], 'New recipient draft');
  assert.equal(submission.sending, false);
  assert.equal(errors.filter(Boolean).length, 1);
});
