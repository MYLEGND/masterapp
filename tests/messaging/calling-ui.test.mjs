import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../SHARED/wwwroot/js/messaging.js', import.meta.url), 'utf8');
const begin = source.indexOf('  function attachCalling(connection)');
const end = source.indexOf('  async function startRealtime()', begin);
const avatarBegin = source.indexOf('  function participantAvatarUrl(person)');
const avatarEnd = source.indexOf('  function createAvatar(', avatarBegin);
const flush = () => new Promise(resolve => setImmediate(resolve));
function fixture() {
  class Element {
    constructor() { this.listeners = {}; this.attrs = {}; this.hidden = false; this.textContent = ''; this.classList = { toggle() {} }; this.dataset = {}; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    setAttribute(name, value) { this.attrs[name] = value; }
    getAttribute(name) { return this.attrs[name]; }
    removeAttribute(name) { delete this.attrs[name]; }
    querySelector(selector) { return element(selector); }
    showModal() { this.open = true; }
    close() { this.open = false; }
    replaceChildren(...children) { this.children = children; this.value = children.find(x => x.selected)?.value; }
    play() { return Promise.resolve(); }
  }
  const nodes = new Map(); const element = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  const documentEvents = {}, windowEvents = {}, tones = [], commands = [];
  let client, stopped = 0, saves = 0;
  const connectionEvents = {};
  class Client {
    constructor(options) { Object.assign(this, options); client = this; }
    command(action, extra) { commands.push({ action, extra }); return Promise.resolve({ succeeded: true, preferences: { ringtoneId: 'soft', wallpaperMode: 'profile' }, ringtones: [{ id: 'soft', label: 'Soft', resource: 'legend_ringback' }], wallpapers: [{ id: 'profile', label: 'Photo' }] }); }
    retire() { this.retired = true; this.call = null; this.present(null); this.media(null, null); }
    start(...args) { this.starts = (this.starts || 0) + 1; this.lastStart=args; return Promise.resolve(); }
    end() { return Promise.resolve(); }
    fail(error) { throw error; }
  }
  const context = {
    window: { LegendBrowserCalling: Client, addEventListener: (name, callback) => windowEvents[name] = callback },
    document: { getElementById: element, addEventListener: (name, callback) => documentEvents[name] = callback, querySelectorAll: () => [] },
    initials: value => value.split(' ').map(x=>x[0]).join('').slice(0,2),
    crypto: { randomUUID: () => 'device-one' }, applicationCopy: value => value, URL,
    location: { origin: 'https://client.example', href: 'https://client.example/messages' },
    HTMLFormElement: Element, localStorage: { setItem: () => saves++ },
    Option: function(text, value, _, selected) { return { text, value, selected }; },
    Audio: class { constructor(url) { this.url = url; tones.push(this); } play() { return Promise.resolve(); } pause() { this.paused = true; } },
    isCurrentParticipant: id => id === 'me', state: { active: { id: 'conversation' }, isOpen: true },
    cancelCallSelection() {}, beginCallSelection() {},
    elements: { threadTitle: { textContent: 'Recipient' } }, showError() {}, openCommandCenter: () => Promise.resolve(),
    connection: { onreconnected(callback) { connectionEvents.reconnected = callback; }, onclose() {}, stop() { stopped++; return Promise.resolve(); } }
  };
  vm.runInNewContext(source.slice(avatarBegin, avatarEnd) + source.slice(begin, end) + '\nattachCalling(connection);', context);
  return { element, client, context, connectionEvents, windowEvents, documentEvents, tones, commands, stopped: () => stopped, saves: () => saves, Element };
}
test('web uses server receipt state, recipient name and the existing authorized avatar endpoint', () => {
  const f = fixture();
  const call = { id: 'call-a', status: 'ringing', calleeName: 'Recipient One', calleeUserId: 'other/user', calleeType: 'Client', calleeWallpaperMode: 'profile' };
  f.client.call = call; f.client.present(call, true);
  assert.equal(f.element('legendBrowserCallName').textContent, 'Recipient One');
  assert.equal(f.element('legendBrowserCallStatus').textContent, 'Calling');
  assert.equal(f.element('legendBrowserCallPortrait').src, '/Messaging/Participants/other%2Fuser/Avatar?participantType=Client');
  assert.equal(f.tones[0].url, '/_content/Shared/calling/legend_ringback.wav');
  f.client.present({ ...call, receivedUtc: '2026-01-01T00:00:00Z' }, true);
  assert.equal(f.element('legendBrowserCallStatus').textContent, 'Ringing');
  f.client.present(null);
  assert.equal(f.tones[0].paused, true);
  assert.equal(f.element('legendBrowserCall').open, false);
});
test('incoming ringtone is selected by the central snapshot rather than a client preference store', () => {
  const f = fixture();
  f.client.call = { id: 'b' };
  f.client.present({ id: 'b', status: 'ringing', callerName: 'Caller', incomingRingtoneResource: 'legend_ringback' }, false);
  assert.equal(f.tones[0].url, '/_content/Shared/calling/legend_ringback.wav');
});
test('calling profile reads available choices and saves only via the existing Call command', async () => {
  const f = fixture();
  await f.element('messagingCallingProfile').listeners.click();
  assert.equal(f.commands[0].action, 'preferences');
  assert.equal(f.element('legendCallingRingtone').children.length, 1);
  assert.equal(f.element('legendCallingRingtone').value, 'soft');
  await f.element('legendCallingPreferencesForm').listeners.submit({ preventDefault() {} });
  assert.equal(f.commands[1].extra.preferences.ringtoneId, 'soft');
  assert.equal(f.commands[1].extra.preferences.wallpaperMode, 'profile');
  assert.match(f.element('legendCallingPreferencesStatus').textContent, /saved/);
});
test('logout immediately retires call UI and socket without waiting for media cleanup acknowledgements', async () => {
  const f = fixture(); const form = new f.Element(); form.action = '/Account/Logout';
  f.client.end = () => new Promise(() => {});
  f.documentEvents.submit({ target: form });
  assert.equal(f.client.retired, true); assert.equal(f.stopped(), 1); assert.equal(f.saves(), 1);
  f.element('messagingVoiceCall').listeners.click();
  await f.element('messagingCallingProfile').listeners.click();
  assert.equal(f.client.starts || 0, 0); assert.equal(f.commands.length, 0);
  f.client.present({ id: 'late', status: 'ringing' }, false);
  assert.notEqual(f.element('legendBrowserCall').open, true);
});
test('sibling-tab logout retires without BroadcastChannel and stale media errors do not reopen UI', async () => {
  const f = fixture();
  f.windowEvents.storage({ key: 'legend-session-retirement', newValue: 'new-session-event' });
  assert.equal(f.client.retired, true); assert.equal(f.stopped(), 1);
  f.client.media({}, {}); await flush();
  assert.equal(f.element('legendBrowserCallRemote').srcObject, null);
});

test('late reconnect rejection retains its original call scope', async () => {
  const f = fixture(); let rejectSync, reported;
  const original = { id: 'call-a', generation: 1 };
  f.client.scope = () => original;
  f.client.sync = () => new Promise((_, reject) => { rejectSync = reject; });
  f.client.fail = (error, scope) => { reported = scope; };
  f.connectionEvents.reconnected();
  f.client.call = { id: 'call-b' }; f.client.scope = () => ({ id: 'call-b', generation: 2 });
  rejectSync(new Error('old reconnect failed')); await flush();
  assert.equal(reported, original);
});

test('back-forward cache restoration reauthenticates a retired document', () => {
  const f = fixture(); let reloads = 0; f.context.location.reload = () => reloads++;
  f.windowEvents.pagehide(); f.windowEvents.pageshow({ persisted: true });
  assert.equal(f.client.retired, true); assert.equal(reloads, 1);
  f.windowEvents.pageshow({ persisted: false }); assert.equal(reloads, 1);
});

test('chat voice and video buttons call the existing client directly with their selected mode',()=>{
 const f=fixture();f.element('messagingVoiceCall').listeners.click();assert.deepEqual(f.client.lastStart,['conversation',false,'Recipient']);f.element('messagingVideoCall').listeners.click();assert.deepEqual(f.client.lastStart,['conversation',true,'Recipient']);assert.equal(f.client.starts,2);
});

for (const caller of [true, false]) {
  test(`accepted ${caller ? 'outgoing' : 'incoming'} call clears identity and ringing without waiting for video`, () => {
    const f = fixture();
    const call = { id: 'phase-call', status: 'ringing', calleeName: 'Peer', callerName: 'Peer',
      calleeUserId: 'peer', callerUserId: 'peer', calleeType: 'Client', callerType: 'Client',
      incomingRingtoneResource: 'legend_incoming' };
    f.client.call = call;
    f.client.present(call, caller);
    assert.equal(f.element('legendBrowserCallName').hidden, false);
    assert.equal(f.element('legendBrowserCallInitials').hidden, false);
    assert.equal(f.element('legendBrowserCallPortrait').hidden, false);
    for (const status of ['connecting', 'active']) {
      f.client.present({ ...call, status }, caller);
      assert.equal(f.element('legendBrowserCallName').hidden, true);
      assert.equal(f.element('legendBrowserCallInitials').hidden, true);
      assert.equal(f.element('legendBrowserCallPortrait').hidden, true);
      assert.equal(f.element('[data-legend-call-action="accept"]').hidden, true);
      assert.equal(f.tones[0].paused, true);
      assert.equal(f.tones.length, 1);
    }
    f.client.present(null);
    f.client.present({ ...call, id: 'next-call', calleeUserId: 'new-peer' }, caller);
    assert.equal(f.element('legendBrowserCallName').hidden, false);
    assert.equal(f.element('legendBrowserCallInitials').hidden, false);
    assert.equal(f.element('legendBrowserCallPortrait').hidden, false);
  });
}
