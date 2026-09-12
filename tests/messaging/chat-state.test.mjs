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
    state: { conversations:[], active:null, requestedConversationId:null, navigationVersion:0, detailFlights:new Map(), detailRevisions:new Map(), readFlights:new Map(), reactionFlights:new Map(), isOpen:true, readAcknowledged:new Map(), scrollPositions:{}, inboxDirty:false, inboxFlight:null },
    AbortController, applicationCopy:value=>value,
    document:{hidden:false},
    elements:{newMessages:{hidden:false},messages:{scrollTop:0}}, request,
    isConversationInRecipientScope:()=>true,
    saveDraft(){},writeSession(){},removeSession(){},renderConversation(){},renderConversations(){},renderSearchResults(){},setUnreadCount(){},showError(){},
    isCurrentParticipant:(id,type)=>id==='self'&&type==='Client',parseUtcTimestamp:value=>value?new Date(value):null
  };
  vm.createContext(context);
  for (const name of ['waitForSelectedDetail','cancelDetailRequests','clearUnavailableConversation','selectDraftRecipient','latestReadMessageIndex','refreshList','acknowledgeVisibleConversation','loadConversation']) vm.runInContext(implementation(name), context);
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
  constructor(tag) { this.tagName=tag;this.children=[];this.events={};this.attributes={};this.dataset={}; }
  append(...children) { this.children.push(...children); }
  setAttribute(key,value) { this.attributes[key]=value; }
  addEventListener(name,handler) { this.events[name]=handler; }
  focus() { this.focused=true; }
  // Match the DOM operations used to anchor reactions inside shared content.
  querySelector(selector) {
    const name=selector.slice(1);
    for(const child of this.children) {
      if ((child.className || '').split(' ').includes(name)) return child;
      const nested=child.querySelector?.(selector); if(nested) return nested;
    }
    return null;
  }
  get classList() { return {add: name => { this.className = `${this.className || ''} ${name}`.trim(); }}; }
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
  const imageLink=children.find(x=>x.className==='messaging-shared-original');
  assert.equal(imageLink.children[0].src,'/Social/Media/asset-id');
  assert.equal(imageLink.href,'/Social/Posts/post-id');
  assert.equal(children.find(x=>x.tagName==='a').href,'/Social/Posts/post-id');
  const unavailable=new Element('article');c.appendSharedContent(unavailable,{sourcePostId:'post-id',status:'unavailable',media:[{id:'secret',mediaKind:'Image'}]});
  assert.equal(unavailable.children[0].children.filter(x=>x.tagName==='img'||x.tagName==='a').length,0);
});
test('reaction controls consume server palette and double tap explicitly sets red heart',async()=>{
  const calls=[];const c=domEnvironment(async(url,options)=>{calls.push({url,options});return {messageId:'m',reactions:[{emoji:'❤️',count:1,reactedByCurrentActor:true}]};});
  c.state.active={id:'A',messages:[{id:'m'}]};
  const card=new Element('article');c.appendMessageInteractions(card,{id:'A',reactionOptions:['🦉']},{id:'m',reactions:[]});
  const menu=card.children[0],palette=menu.children[1];
  assert.equal(palette.children[0].textContent,'🦉');
  card.events.dblclick({target:{closest:()=>false}});
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls.length,1);assert.equal(calls[0].options.method,'PUT');assert.equal(JSON.parse(calls[0].options.body).emoji,'❤️');
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
  assert.equal(c.state.active.messages[0].reactions[0].emoji, '❤️', 'An older acknowledgment must not replace the newer pending choice');
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
  const c = domEnvironment(request);
  c.elements.error = new Element('div');
  Object.assign(c.state, { pendingSubmissions: new Map(), pendingSubmission: null, drafts: {}, draftTarget: null });
  Object.assign(c.elements, { messageBody: { value: 'Submitted body' }, files: { files: [], value: '' }, sendButton: { disabled: false } });
  c.FormData = FormData;
  c.token = null;
  c.renderSelectedFiles = () => {};
  c.participantIdentityKey = (id, type) => `${type}:${id}`;
  let identity = 0;
  c.clientMessageId = () => `test-submission-${++identity}`;
  for (const name of ['activeDraftKey', 'saveDraft', 'createSubmission', 'uploadAttachments', 'offerPendingRetry', 'sendMessage'])
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
test('pending attachment retry is explicit and never substitutes newly selected files', async()=>{
  let uploads=0,messages=0;const uploaded=[];
  const c=submissionEnvironment(async(url,options)=>{
    if(url.endsWith('/Messages')) { messages++;return {message:{id:'committed'}}; }
    if(url.endsWith('/Attachments')) { uploaded.push(await options.body.get('file').text());if(++uploads===1)throw Error('offline');return {}; }
    if(url==='/Messaging/Conversations')return {conversations:[]};
    return {conversation:{id:'A',messages:[{id:'committed'}]}};
  });
  c.state.active={id:'A',messages:[]};
  c.elements.files.files=[new Blob(['original'])];
  await c.sendMessage();
  c.elements.messageBody.value='New draft';c.elements.files.files=[new Blob(['new file'])];c.saveDraft();
  await c.sendMessage();
  assert.equal(uploads,1);
  const retry=c.elements.error.children.at(-1);
  await retry.events.click();await flushTasks();
  assert.deepEqual(uploaded,['original','original']);assert.equal(messages,1);
  assert.equal(c.elements.messageBody.value,'New draft');
  assert.equal(await c.elements.files.files[0].text(),'new file');
});

// Immediate feedback is provisional; a failed server mutation must restore it.
test('reaction appears before network completion and rolls back on failure', async () => {
  const pending=deferred(), c=domEnvironment(()=>pending.promise);
  const message={id:'m',reactions:[]}; c.state.active={id:'A',messages:[message]};
  const operation=c.setMessageReaction('A',message,'❤️');
  assert.equal(c.state.active.messages[0].reactions[0].emoji,'❤️');
  pending.reject(new Error('offline')); await operation;
  assert.equal(c.state.active.messages[0].reactions.length,0);
});
test('selecting a palette reaction dismisses the menu before response', async () => {
  const pending=deferred(), c=domEnvironment(()=>pending.promise);
  const message={id:'m',reactions:[]}; c.state.active={id:'A',messages:[message]};
  const card=new Element('article'); c.appendMessageInteractions(card,{id:'A',reactionOptions:['❤️']},message);
  const menu=card.children[0]; menu.open=true;
  menu.children[1].children[0].events.click();
  assert.equal(menu.open,false);
  pending.resolve({messageId:'m',reactions:[{emoji:'❤️',count:1,reactedByCurrentActor:true}]});
  await new Promise(resolve=>setImmediate(resolve));
});

test('two failed queued choices restore the last confirmed server reactions', async () => {
  const first=deferred(), second=deferred(); let calls=0;
  const c=domEnvironment(()=>++calls===1?first.promise:second.promise);
  const message={id:'m',reactions:[]}; c.state.active={id:'A',messages:[message]};
  const a=c.setMessageReaction('A',message,'👍');
  const b=c.setMessageReaction('A',message,'❤️');
  first.reject(new Error('offline')); await a;
  second.reject(new Error('offline')); await b;
  assert.equal(c.state.active.messages[0].reactions.length,0);
});


test('thread selection renders its known shell before detail resolves and requests only a bounded recent page', async () => {
  const detail=deferred(); const calls=[]; const c=environment((url, options)=>{calls.push({url,options});return detail.promise;});
  c.state.conversations=[{id:'A',displayTitle:'Known contact'}];
  let rendered; c.renderConversation=()=>{rendered=c.state.active;};
  const opening=c.loadConversation('A',false);
  assert.equal(rendered.id,'A'); assert.equal(rendered.displayTitle,'Known contact');
  assert.equal(rendered.isDetailPending,true); assert.equal(rendered.messages.length,0);
  assert.equal(calls[0].url,'/Messaging/Conversations/A?take=60');
  assert.equal(calls[0].options.signal.aborted,false);
  detail.resolve({conversation:{id:'A',messages:[{id:'latest'}]}}); await opening;
  assert.equal(c.state.active.messages[0].id,'latest'); assert.equal(c.state.active.isDetailPending,undefined);
});
test('refresh retains existing content while switching cancels previous transport', async () => {
  const first=deferred(), second=deferred(); const signals=[];
  const c=environment((url,options)=>{signals.push(options.signal);return url.includes('/A?')?first.promise:second.promise;});
  const existing={id:'A',messages:[{id:'already-visible'}]}; c.state.active=existing;
  const refreshing=c.loadConversation('A',false);
  assert.equal(c.state.active,existing);
  const switching=c.loadConversation('B',false);
  assert.equal(signals[0].aborted,true);assert.equal(c.state.active.id,'B');
  first.reject(Object.assign(new Error('cancelled'),{name:'AbortError'}));await refreshing;
  second.resolve({conversation:{id:'B',messages:[]}});await switching;
});
test('failed initial detail leaves a retryable shell instead of a permanent loading indicator', async () => {
  const c=environment(async()=>{throw new Error('offline');});
  await assert.rejects(c.loadConversation('A',false),/offline/);
  assert.equal(c.state.active.detailLoadFailed,true);assert.equal(c.state.active.isDetailPending,true);
});
test('restored detail cannot enter a different recipient scope', async () => {
  const c=environment(async()=>({conversation:{id:'A',participants:[{userId:'other',participantType:'Client'}],messages:[{id:'wrong-scope'}]}}));
  c.recipientScopeParticipantType=()=> 'Agent';
  vm.runInContext(implementation('currentCounterparty'),c);
  vm.runInContext(implementation('isConversationInRecipientScope'),c);
  await c.loadConversation('A',false);
  assert.equal(c.state.active,null);assert.equal(c.state.requestedConversationId,null);
});
test('opening command center starts restored detail without waiting for inbox or directory', async () => {
  const c=environment(()=>{});const calls=[];const held=deferred();
  const classes={add(){},remove(){}};
  c.root={hidden:true,setAttribute(){},classList:classes};c.document={body:{classList:classes},activeElement:null};
  c.elements.window={focus(){}};c.unreadBadges=[];c.state.isOpen=false;
  c.markCommandCenterOpen=()=>{};c.readSession=()=> 'restored';
  c.loadConversation=(id)=>{calls.push('detail:'+id);return held.promise;};
  c.refreshList=()=>{calls.push('inbox');return held.promise;};
  c.loadRecipients=()=>{calls.push('directory');return held.promise;};
  vm.runInContext(implementation('openCommandCenter'),c);
  await c.openCommandCenter(null);
  assert.equal(c.root.hidden,false);assert.equal(c.state.isOpen,true);assert.equal(c.state.isOpening,false);
  assert.deepEqual(calls,['detail:restored','inbox','directory']);
  held.resolve();
});


test('scope matching supports detail participants as well as inbox counterparty summaries', () => {
  const c=environment(()=>{});c.recipientScopeParticipantType=()=> 'Agent';
  vm.runInContext(implementation('currentCounterparty'),c);
  vm.runInContext(implementation('isConversationInRecipientScope'),c);
  assert.equal(c.isConversationInRecipientScope({counterparty:{participantType:'Agent'}}),true);
  assert.equal(c.isConversationInRecipientScope({participants:[{userId:'self',participantType:'Client'},{userId:'other',participantType:'Agent'}]}),true);
  assert.equal(c.isConversationInRecipientScope({participants:[{userId:'other',participantType:'Client'}]}),false);
});


test('missing realtime library starts existing polling fallback without blocking chat', async () => {
  const c=environment(()=>{});c.window={};let polling=0;c.startPolling=()=>polling++;
  vm.runInContext(implementation('startRealtime'),c);
  await c.startRealtime();
  assert.equal(polling,1);assert.notEqual(c.state.realtimeStarted,true);
});


test('older history sends timestamp and message identity to preserve equal-time rows', async () => {
  const calls=[];const c=environment(async url=>{calls.push(url);return {conversation:{messages:[],hasOlderMessages:false}};});
  c.state.active={id:'A',hasOlderMessages:true,messages:[{id:'boundary-id',sentUtc:'2026-09-11T00:00:00Z'}]};
  c.elements.messages.scrollHeight=100;
  vm.runInContext(implementation('loadOlderMessages'),c);
  await c.loadOlderMessages({disabled:false});
  assert.match(calls[0],/beforeUtc=2026-09-11T00%3A00%3A00Z&beforeMessageId=boundary-id/);
});


test('messaging transitions from polling to one realtime connection after delayed library arrival', async () => {
  const c=environment(()=>{});c.window={};let polling=0,stopped=0,started=0;
  c.startPolling=()=>polling++;c.stopPolling=()=>stopped++;
  vm.runInContext(implementation('startRealtime'),c);
  await c.startRealtime();assert.equal(polling,1);
  const connection={on(){},onreconnecting(){},onreconnected(){},onclose(){},start:async()=>started++};
  c.window.signalR={HubConnectionBuilder:class {withUrl(){return this;}withAutomaticReconnect(){return this;}build(){return connection;}}};
  await Promise.all([c.startRealtime(),c.startRealtime()]);
  assert.equal(started,1);assert.equal(stopped,1);
});


for (const status of [401,403,404,410]) test(`authoritative HTTP ${status} clears retained thread and inbox metadata`, async () => {
  const c=environment(async()=>{throw Object.assign(new Error('Access unavailable'),{status});});
  c.state.active={id:'A',messages:[{id:'private-content'}]};c.state.conversations=[{id:'A',lastMessagePreview:'Private preview'},{id:'B'}];
  c.state.readAcknowledged.set('A','private-content');let removed;c.removeSession=key=>removed=key;
  await assert.rejects(c.loadConversation('A',false),/Access unavailable/);
  assert.equal(c.state.active,null);assert.equal(c.state.requestedConversationId,null);
  assert.equal(c.state.conversations.length,1);assert.equal(c.state.conversations[0].id,'B');
  assert.equal(c.state.readAcknowledged.has('A'),false);assert.equal(removed,'last-conversation');
});
test('transient refresh failure keeps existing messages available', async () => {
  const c=environment(async()=>{throw Object.assign(new Error('Temporarily unavailable'),{status:503});});
  const existing={id:'A',messages:[{id:'retained'}]};c.state.active=existing;
  await assert.rejects(c.loadConversation('A',false));assert.equal(c.state.active,existing);
});
test('revocation from superseded navigation cannot clear the newly selected pane', async () => {
  const held=deferred();const c=environment(url=>url.includes('/A?')?held.promise:Promise.resolve({conversation:{id:'B',messages:[{id:'current'}]}}));
  const first=c.loadConversation('A',false);await c.loadConversation('B',false);
  held.reject(Object.assign(new Error('Forbidden'),{status:403}));await first;
  assert.equal(c.state.active.id,'B');assert.equal(c.state.active.messages[0].id,'current');
});
test('HTTP wrapper preserves status while transport failures remain statusless', async () => {
  const c=environment(()=>{});c.requestHeaders=()=>({});c.fetch=async()=>({ok:false,status:403,json:async()=>({errorMessage:'Forbidden'})});
  vm.runInContext(implementation('request'),c);
  await assert.rejects(c.request('/test'),error=>error.status===403&&error.message==='Forbidden');
  c.fetch=async()=>{throw new TypeError('offline');};
  await assert.rejects(c.request('/test'),error=>error.status===undefined&&/temporarily unavailable/.test(error.message));
});


test('opening a cached out-of-scope last conversation does not request its detail', async () => {
  const c=environment(()=>{});let details=0;const classes={add(){}};
  c.root={hidden:true,setAttribute(){},classList:classes};c.document={body:{classList:classes},activeElement:null};
  c.elements.window={focus(){}};c.unreadBadges=[];c.state.isOpen=false;c.markCommandCenterOpen=()=>{};
  c.readSession=()=> 'cached';c.state.conversations=[{id:'cached',counterparty:{participantType:'Client'}}];
  c.recipientScopeParticipantType=()=> 'Agent';
  vm.runInContext(implementation('currentCounterparty'),c);
  vm.runInContext(implementation('isConversationInRecipientScope'),c);
  c.loadConversation=async()=>details++;c.refreshList=async()=>{};c.loadRecipients=async()=>{};
  vm.runInContext(implementation('openCommandCenter'),c);
  await c.openCommandCenter(null);assert.equal(details,0);assert.equal(c.state.active,null);
});

test('selected detail is fetched before background inbox and contact directory work', async () => {
  const detail=deferred();const calls=[];
  const c=environment(url=>{calls.push(url);return url.includes('/A?')?detail.promise:Promise.resolve({conversations:[],recipients:[]});});
  c.recipientRequestUrl=()=>'/Messaging/Recipients';c.state.recipientScope='Agents';
  vm.runInContext(implementation('loadRecipients'),c);
  const selected=c.loadConversation('A',false);const inbox=c.refreshList();const contacts=c.loadRecipients();
  await new Promise(resolve=>setImmediate(resolve));assert.deepEqual(calls,['/Messaging/Conversations/A?take=60']);
  detail.resolve({conversation:{id:'A',messages:[]}});await Promise.all([selected,inbox,contacts]);
  assert.equal(calls.length,3);
});
test('selecting a chat aborts background inbox transport and retries it after selected detail', async () => {
  const oldInbox=deferred(),detail=deferred();const calls=[];let oldSignal;
  const c=environment((url,options)=>{calls.push(url);if(url.includes('/A?'))return detail.promise;if(calls.length===1){oldSignal=options.signal;return oldInbox.promise;}return Promise.resolve({conversations:[{id:'fresh'}]});});
  const inbox=c.refreshList();await new Promise(resolve=>setImmediate(resolve));
  const selected=c.loadConversation('A',false);assert.equal(oldSignal.aborted,true);
  oldInbox.resolve({conversations:[{id:'obsolete'}]});await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls.length,2);assert.equal(c.state.conversations.length,0);
  detail.resolve({conversation:{id:'A',messages:[]}});await Promise.all([selected,inbox]);
  assert.equal(calls.length,3);assert.equal(c.state.conversations[0].id,'fresh');
});
test('safe text links preserve internal navigation and isolate external destinations', () => {
  const c=domEnvironment(()=>{});c.URL=URL;c.window.location={href:'https://portal.example/chat',origin:'https://portal.example'};
  c.document.createTextNode=value=>({textContent:value,tagName:'#text'});
  vm.runInContext(implementation('appendLinkedText'),c);
  const content=new Element('p');
  c.appendLinkedText(content,'Visit https://portal.example/Social/Posts/one and https://outside.example/path. javascript:alert(1)');
  const links=content.children.filter(node=>node.tagName==='a');
  assert.equal(links.length,2);assert.equal(links[0].target,undefined);assert.equal(links[1].target,'_blank');assert.equal(links[1].rel,'noopener noreferrer');
  assert.equal(links[1].href,'https://outside.example/path');
});

test('shared content owns its reaction badges rather than the surrounding message row', () => {
  const c=domEnvironment(()=>{}), card=new Element('article'), shared=new Element('div');
  shared.className='messaging-shared-content';card.append(shared);
  c.appendMessageInteractions(card,{id:'A',reactionOptions:['❤️']},{id:'m',reactions:[{emoji:'❤️',count:1}]});
  assert.equal(shared.children.at(-1).className,'messaging-reactions');
  assert.equal(card.children.some(x=>x.className==='messaging-reactions'),false);
});
