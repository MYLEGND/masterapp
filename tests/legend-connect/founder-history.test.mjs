import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/legend-founder-ai.js', import.meta.url), 'utf8');
function implementation(name) {
  const start = source.search(new RegExp(`^    (?:async )?function ${name}\\(`, 'm'));
  assert.notEqual(start, -1, name);
  const tail = source.slice(start);
  const end = tail.search(/\n    }\n/);
  assert.notEqual(end, -1, name);
  return tail.slice(0, end + 6);
}
function environment(fetch) {
  const conversation = { id: 'thread', persisted: true, messages: [], lastMessageId: null, mode: 'legend' };
  const c = { AbortController, URLSearchParams, Set, Map, TextDecoder, DOMException, crypto: { randomUUID: () => 'new-id' },
    HISTORY_URL: '/founder/legend-ai/conversations', HISTORY_REFRESH_MS: 20000,
    busy: false, historyRequest: null, historyTimer: null, historySkip: 0, historyHasMore: false,
    accountGeneration: 0, activeRequest: null, state: { activeConversationId: 'thread', conversations: [conversation] },
    document: { hidden: false }, modalElement: { dataset: { chatUrl: '/chat' }, classList: { contains: () => true } },
    window: { clearTimeout() {}, setTimeout() { return 1; } },
    form: { querySelector: () => ({ value: 'csrf' }) }, input: { value: 'Unsent draft' },
    founderCommandConfirmed: { checked: false }, status: { textContent: '' }, fetch,
    activeConversation: () => c.state.conversations.find(row => row.id === c.state.activeConversationId),
    newConversationRecord: () => ({ messages: [], mode: 'legend' }),
    defaultState: () => ({ activeConversationId: 'fresh', conversations: [{ id: 'fresh', messages: [] }] }),
    setBusy(value) { c.busy = value; }, renderAll() {}, applyOperationalProgress() {},
  };
  vm.createContext(c);
  for (const name of ['stopHistoryRefresh', 'clearAuthenticatedHistory', 'readHistory', 'storedMessage',
    'loadConversationPage', 'refreshHistory', 'scheduleHistoryRefresh', 'structuredFailureMessage',
    'consumeChatResultStream', 'executeConversationRequest']) vm.runInContext(implementation(name), c);
  return c;
}
const ok = value => ({ ok: true, status: 200, json: async () => value });
const row = (id, time = '2026-09-13T10:00:00.1234567Z') => ({ id, body: id, sentUtc: time, authorKind: 'Human' });
const page = (messages, hasOlderMessages = true) => ({ succeeded: true, conversation: { messages, hasOlderMessages, lastMessageUtc: messages.at(-1)?.sentUtc } });
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

test('canonical paging retains exact UTC cursor and older rows across overlapping refresh', async () => {
  const urls = [];
  let response = page([row('b'), row('c')]);
  const c = environment(async url => { urls.push(url); return ok(response); });
  const conversation = c.activeConversation();
  const signal = new AbortController().signal;
  await c.loadConversationPage(conversation, signal);
  response = page([row('a')], false);
  await c.loadConversationPage(conversation, signal, true);
  assert.equal(new URL(urls.at(-1), 'https://test').searchParams.get('beforeUtc'), '2026-09-13T10:00:00.1234567Z');
  assert.equal(new URL(urls.at(-1), 'https://test').searchParams.get('beforeMessageId'), 'b');
  response = page([row('b'), row('c')]);
  await c.loadConversationPage(conversation, signal);
  assert.equal(conversation.messages.map(item => item.id).join(','), 'a,b,c');
  assert.equal(conversation.lastMessageId, 'c');
  assert.equal(conversation.hasOlder, false);
});

test('disconnected newest page requires explicit window change and retains reachable older cursor', async () => {
  let response = page([row('a'), row('b')], false);
  const c = environment(async () => ok(response));
  const conversation = c.activeConversation();
  const signal = new AbortController().signal;
  await c.loadConversationPage(conversation, signal);
  response = page([row('y'), row('z')], true);
  await c.loadConversationPage(conversation, signal);
  assert.equal(conversation.messages.map(item => item.id).join(','), 'a,b');
  assert.equal(conversation.lastMessageId, 'b');
  assert.equal(conversation.hasNewer, true);
  response = page([row('earlier')], false);
  await c.loadConversationPage(conversation, signal, true);
  assert.equal(conversation.hasNewer, true, 'Loading older messages cannot close a gap at the newer end');
  response = page([row('y'), row('z')], true);
  await c.loadConversationPage(conversation, signal, false, true);
  assert.equal(conversation.messages.map(item => item.id).join(','), 'y,z');
  assert.equal(conversation.lastMessageId, 'z');
  assert.equal(conversation.hasOlder, true);
  assert.equal(conversation.hasNewer, false);
});

test('periodic head refresh preserves the loaded list offset', async () => {
  const urls = [];
  const c = environment(async url => { urls.push(url); return ok({ succeeded: true, conversations: Array.from({ length: 50 }, (_, i) => ({ id: `thread-${i}` })) }); });
  c.activeConversation().persisted = false;
  c.historySkip = 100;
  await c.refreshHistory();
  assert.equal(c.historySkip, 100);
  await c.refreshHistory({ more: true });
  assert.equal(new URL(urls.at(-1), 'https://test').searchParams.get('skip'), '100');
  assert.equal(c.historySkip, 150);
});

test('unknown receipt preserves the immutable retry and cancels prior history reads', async () => {
  const requests = [];
  const result = { succeeded: false, mode: 'legend', failureKind: 'outcome_unknown', reason: 'outcome_unknown', error: 'Unknown outcome' };
  const c = environment(async (url, options) => { requests.push({ url, ...options }); return { ok: false, status: 409, text: async () => JSON.stringify(result) }; });
  c.refreshHistory = async () => {};
  const conversation = c.activeConversation();
  const operation = { id: 'immutable-id', body: JSON.stringify({ mode: 'legend', messages: [{ role: 'user', content: 'Original payload' }] }) };
  conversation.pendingOperation = operation;
  const previousRead = new AbortController();
  c.historyRequest = previousRead;
  await c.executeConversationRequest(conversation, operation);
  assert.equal(previousRead.signal.aborted, true);
  assert.equal(conversation.pendingOperation, operation);
  await c.executeConversationRequest(conversation, operation);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].body, requests[1].body);
  assert.equal(requests[0].headers['X-Legend-Ai-Operation-Id'], requests[1].headers['X-Legend-Ai-Operation-Id']);
  assert.equal(conversation.messages.length, 0);
  assert.equal(c.input.value, 'Unsent draft');
});

test('auth replacement invalidates a late POST and cannot restart its refresh', async () => {
  const response = deferred();
  const c = environment(() => response.promise);
  let refreshes = 0;
  c.refreshHistory = async () => { refreshes++; };
  const request = c.executeConversationRequest(c.activeConversation(), { id: 'old-account-operation', body: '{}' });
  c.clearAuthenticatedHistory();
  response.resolve({ ok: false, status: 409, text: async () => JSON.stringify({ succeeded: false, mode: 'legend', error: 'Old account detail' }) });
  await request;
  assert.equal(c.activeRequest, null);
  assert.equal(c.busy, false);
  assert.equal(c.state.activeConversationId, 'fresh');
  assert.equal(c.input.value, '');
  assert.equal(refreshes, 0);
  assert.notEqual(c.status.textContent, 'Old account detail');
});

test('a queued stream chunk cannot update status after account cancellation', async () => {
  const chunk = deferred();
  const c = environment(() => {});
  const controller = new AbortController();
  c.activeRequest = controller;
  let progressUpdates = 0;
  c.applyOperationalProgress = () => { progressUpdates++; };
  const reading = c.consumeChatResultStream({ ok: true, body: { getReader: () => ({ read: () => chunk.promise, cancel: async () => {} }) } }, controller.signal);
  c.clearAuthenticatedHistory();
  chunk.resolve({ done: false, value: new TextEncoder().encode('{"type":"progress","progress":{"message":"Old private activity"}}\n') });
  await assert.rejects(reading, error => error.name === 'AbortError');
  assert.equal(progressUpdates, 0);
});

test('legacy browser transcripts are not read, imported, cleared or sent as authority', () => {
  assert.doesNotMatch(source, /legendFounderAi\.conversations\.v1|localStorage\.removeItem|messages: conversation\.messages/);
  assert.match(source, /expectedLastMessageId: conversation\.lastMessageId/);
  assert.match(source, /messages: \[\{ role: 'user', content: text \}\]/);
  assert.match(source, /metadata\.stage === 'response_partial'/);
});
