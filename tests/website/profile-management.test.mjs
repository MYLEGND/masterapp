import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../Legend-Design/legend-website-management.js', import.meta.url), 'utf8');
const managementCss = readFileSync(new URL('../../Legend-Design/legend-website-management.css', import.meta.url), 'utf8');
const flush = () => new Promise(resolve => setTimeout(resolve, 40));
const deferred = () => {
  let release;
  const promise = new Promise(resolve => { release = resolve; });
  return { promise, release };
};
async function fixture({ caps = {}, failPublish = false, scope = 'business', handoff = false, deferPath = null, deferReply = null, manageFailure = null, publicationBehavior = null, storageEntries = [], publicationScopeId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' } = {}) {
  const dom = new JSDOM(`<button data-website-manage data-session="/profile/session" data-edit="/profile/edit" data-live="https://business.test" data-title="Business A" data-scope="${scope}">Manage</button>`, { url: 'https://client.mylegnd.com/profile', runScripts: 'outside-only' });
  const { window } = dom, calls = [], publications = new Map();
  for (const [key, value] of storageEntries) window.sessionStorage.setItem(key, value);
  window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event('close')); };
  window.confirm = () => true;
  window.URL.createObjectURL = () => 'blob:export'; window.URL.revokeObjectURL = () => {};
  window.HTMLAnchorElement.prototype.click = function () {};
  const state = { publicationScopeId, revision: 7, publishedRevision: 6, document: { version: 1, elements: {} }, capabilities: { canPublish: true, canImport: true, canManageDomains: true, canSchedule: true, canPromote: true, ...caps }, history: [{ versionId: 'version-a', revision: 6, createdUtc: '2026-09-01T10:00:00Z' }], readiness: { checks: [] } };
  let deferredUsed = false, continuationCount = 0;
  window.fetch = async (url, options = {}) => {
    const parsed = new URL(url, window.location.href), body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path: parsed.pathname, url: parsed, options, body });
    if (!deferredUsed && deferReply && parsed.pathname === deferPath) {
      deferredUsed = true;
      await deferReply.promise; // Ignore AbortSignal to prove stale-reply rejection.
    }
    if (publicationBehavior && (parsed.pathname.endsWith('/publish') || parsed.pathname.endsWith('/publication-status'))) {
      const response = await publicationBehavior({ parsed, options, body, state, publications });
      if (response !== undefined) return response;
    }
    if (manageFailure && parsed.pathname === '/api/website-content/manage' && !body)
      return { ok: false, status: manageFailure.status, json: async () => manageFailure.details };
    let value = state;
    if (parsed.pathname === '/profile/session') value = handoff
      ? { handoffUrl: 'https://protect.mylegnd.com/api/website-content/handoff', state: 'opaque-state-' + (++continuationCount) }
      : { ticket: 'signed-scope-a', apiBase: 'https://protect.mylegnd.com' };
    if (parsed.pathname === '/api/website-content/handoff') value = { ticket: 'signed-' + body.state, apiBase: 'https://protect.mylegnd.com' };
    if (parsed.pathname.endsWith('/publish')) {
      if (failPublish) return { ok: false, status: 409, json: async () => ({ message: 'Draft changed elsewhere',
        publicationRejection: { disposition: 'rejected_before_commit', requestId: body.requestId, expectedRevision: body.expectedRevision } }) };
      const existing = publications.get(body.requestId);
      if (existing) value = { ...existing, revision: state.revision };
      else {
        if (state.revision !== body.expectedRevision) return { ok: false, status: 409, json: async () => ({
          error: 'revision_conflict', publicationRejection: { disposition: 'rejected_before_commit',
            requestId: body.requestId, expectedRevision: body.expectedRevision } }) };
        state.revision = body.expectedRevision + 1; state.publishedRevision = state.revision;
        const publication = { versionId: body.requestId, publishedRevision: state.revision };
        publications.set(body.requestId, publication); value = { ...publication, revision: state.revision };
      }
    }
    if (parsed.pathname.endsWith('/publication-status')) {
      const requestId = parsed.searchParams.get('requestId'), expectedRevision = Number(parsed.searchParams.get('expectedRevision'));
      const publication = publications.get(requestId) ?? null;
      value = { publicationScopeId: state.publicationScopeId, requestId, expectedRevision, found: publication !== null, publication, revision: state.revision,
        isCurrent: publication !== null && publication.publishedRevision === state.publishedRevision };
      if (publication === null && state.revision > expectedRevision)
        value.publicationRejection = { disposition: 'rejected_before_commit', requestId, expectedRevision, currentRevision: state.revision };
    }
    if (parsed.pathname.endsWith('/export')) value = { format: 'legend-website-v1', draft: { version: 1, elements: { a: { text: 'Owned content' } } } };
    if (parsed.pathname.endsWith('/import')) value = { report: { sourceUrl: body.sourceUrl, addedComponents: 2, preservedComponents: 4, warnings: ['Booking requires integration'], pages: [{ title: 'Services', url: 'https://old.test/services', forms: ['booking'], warnings: ['Review links'] }] } };
    if (parsed.pathname.endsWith('/business-details')) value = { revision: 7, details: { contactEmail: 'contact@business.test', phone: '123', hours: '9–5', services: 'Repairs', locations: 'Main street' } };
    if (parsed.pathname.endsWith('/domains')) value = options.method === 'POST'
      ? { cnameTarget: 'sites.example.test', binding: { id: 'binding-new', hostname: body.hostname, status: 'pending', certificateStatus: 'pending', verificationJson: JSON.stringify({ ownership: { type: 'TXT', name: '_verify.' + body.hostname, value: 'proof-new' } }) } }
      : { cnameTarget: 'sites.example.test', domains: [{ id: 'binding-a', hostname: 'business.test', status: 'pending', certificateStatus: 'pending', verificationJson: JSON.stringify({ ownership: { type: 'TXT', name: '_verify.business.test', value: 'proof-value' } }) }] };
    if (parsed.pathname === '/api/website-inquiries/manage') value = { inquiries: [{ id: 'inquiry-a', name: '<img src=x onerror=alert(1)>', firstName: 'Avery', lastName: 'Example', phone: '(602) 555-0199', email: 'a@example.test', message: 'Please call', status: 'New' }] };
    return { ok: true, status: 200, json: async () => JSON.parse(JSON.stringify(value)) };
  };
  window.eval(source); window.document.querySelector('[data-website-manage]').click(); await flush();
  const click = async text => { const found = [...window.document.querySelectorAll('button')].find(node => node.textContent.startsWith(text)); assert.ok(found, `button ${text}`); found.click(); await flush(); };
  const storage = () => Array.from({ length: window.sessionStorage.length }, (_, index) => {
    const key = window.sessionStorage.key(index); return [key, window.sessionStorage.getItem(key)];
  });
  const publicationKey = 'legend-website-publication-v1:https://protect.mylegnd.com:' + publicationScopeId;
  const pendingPublication = () => JSON.parse(window.sessionStorage.getItem(publicationKey));
  return { dom, window, document: window.document, calls, click, state, storage, pendingPublication, publications };
}

test('profile session authorizes scope; publish requires confirmation and carries current revision', async () => {
  const f = await fixture();
  assert.equal(f.calls[0].options.credentials, 'same-origin');
  assert.equal(f.calls[1].url.searchParams.get('ticket'), 'signed-scope-a');
  assert.equal(f.calls[1].options.credentials, 'omit');
  await f.click('Publish');
  const sent = f.calls.find(x => x.path.endsWith('/publish')).body;
  assert.match(sent.requestId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.deepEqual(sent, { ticket: 'signed-scope-a', expectedRevision: 7, requestId: sent.requestId });
  const proof = f.calls.find(x => x.path.endsWith('/publication-status'));
  assert.equal(proof.options.method, undefined);
  assert.equal(proof.url.searchParams.get('requestId'), sent.requestId);
  assert.equal(proof.url.searchParams.get('expectedRevision'), '7');
  assert.equal(f.pendingPublication().phase, 'review');
  assert.match(f.document.querySelector('[role=status]').textContent, /Publication recorded/);
  assert.doesNotMatch(f.document.querySelector('[role=status]').textContent, /Website published|verified live/i);
  f.dom.window.close();
});

test('editor permissions hide owner-only operations while keeping draft editor', async () => {
  const f = await fixture({ caps: { canPublish: false, canImport: false, canManageDomains: false, canSchedule: false } });
  const editorLink = [...f.document.querySelectorAll('a')].find(a => new URL(a.href, f.window.location.href).pathname === '/profile/edit');
  assert.ok(editorLink);
  assert.equal(new URL(editorLink.href, f.window.location.href).searchParams.get('legendEdit'), 'signed-scope-a');
  for (const text of ['Publish', 'Domains', 'Schedule', 'Import']) assert.equal([...f.document.querySelectorAll('button')].some(n => n.textContent.trim() === text), false);
  await f.click('History'); assert.equal([...f.document.querySelectorAll('button')].some(n => n.textContent === 'Restore'), false); f.dom.window.close();
});

test('revision conflict retains workspace and displays server error without false success', async () => {
  const f = await fixture({ failPublish: true }); await f.click('Publish');
  assert.match(f.document.querySelector('[role=status]').textContent, /Draft changed elsewhere.*Review/); assert.ok(f.document.querySelector('dialog').open);
  assert.equal(f.pendingPublication().phase, 'review'); f.dom.window.close();
});

test('authorized import accepts own export draft and displays actionable migration report', async () => {
  const f = await fixture(); await f.click('Import');
  const file = f.document.querySelector('input[type=file]');
  Object.defineProperty(file, 'files', { value: [{ size: 20, text: async () => JSON.stringify({ draft: { version: 1, elements: { a: { text: 'Owned content' } } } }) }] });
  await f.click('Import draft'); assert.equal(f.calls.some(c => c.path.endsWith('/import')), false);
  f.document.querySelector('input[type=checkbox]').checked = true;
  f.document.querySelector('input[type=url]').value = 'https://old.test'; await f.click('Import draft');
  const request = f.calls.find(c => c.path.endsWith('/import')).body;
  assert.equal(request.document.elements.a.text, 'Owned content'); assert.equal(request.authorized, true); assert.equal(request.expectedRevision, 7);
  assert.match(f.document.body.textContent, /Booking requires integration/); assert.match(f.document.body.textContent, /4 existing components preserved/); assert.match(f.document.body.textContent, /Review links/); f.dom.window.close();
});

test('domain guidance shows only the registrar routing record and keeps provider validation internal', async () => {
  const f = await fixture(); await f.click('Domains');
  const values = [...f.document.querySelectorAll('input')].map(input => input.value);
  assert.ok(values.includes('CNAME'));
  assert.ok(values.includes('@'));
  assert.ok(values.includes('sites.example.test'));
  assert.equal(values.includes('proof-value'), false);
  assert.equal(f.document.querySelector('pre'), null);
  await f.click('Verify status');
  assert.equal(f.calls.find(c => c.path.endsWith('/domains/refresh')).body.bindingId, 'binding-a'); f.dom.window.close();
});

test('connect domain returns one registrar-ready routing record on the first request', async () => {
  const f = await fixture(); await f.click('Domains');
  const domainInput = [...f.document.querySelectorAll('input')].find(input => !input.readOnly && input.type === 'text');
  domainInput.value = 'example.com';
  await f.click('Connect domain');
  const request = f.calls.find(call => call.path.endsWith('/domains') && call.options.method === 'POST');
  assert.equal(request.body.hostname, 'example.com');
  const values = [...f.document.querySelectorAll('input')].map(input => input.value);
  assert.ok(values.includes('CNAME'));
  assert.ok(values.includes('@'));
  assert.ok(values.includes('sites.example.test'));
  assert.match(f.document.body.textContent, /add exactly this record/i);
  assert.equal(/ALIAS|ANAME|flattening/i.test(f.document.body.textContent), false);
  f.dom.window.close();
});

test('schedule sends ISO time and current draft revision', async () => {
  const f = await fixture(); await f.click('Schedule');
  f.document.querySelector('input[type=datetime-local]').value = '2099-01-02T12:30'; await f.click('Schedule draft');
  const body = f.calls.find(c => c.path.endsWith('/schedule')).body; assert.equal(body.expectedRevision, 7); assert.match(body.publishUtc, /^2099-01-02T/); f.dom.window.close();
});

test('business inbox shows canonical split name phone and email as text and updates selected inquiry only', async () => {
  const f = await fixture(); await f.click('Inquiries'); assert.equal(f.document.querySelector('img'), null);
  assert.match(f.document.body.textContent, /Avery Example/);
  assert.match(f.document.body.textContent, /\(602\) 555-0199/);
  assert.match(f.document.body.textContent, /a@example\.test/);
  f.document.querySelector('select').value = 'Contacted'; await f.click('Save status');
  assert.deepEqual(f.calls.find(c => c.path.endsWith('/manage/status')).body, { ticket: 'signed-scope-a', inquiryId: 'inquiry-a', status: 'Contacted' }); f.dom.window.close();
});

test('agent scope reuses the already-authorized management session for direct editor entry', async () => {
  const f = await fixture({ scope: 'agent' });
  const link = [...f.document.querySelectorAll('a')].find(a => a.textContent === 'Open editor');
  assert.ok(link);
  const url = new URL(link.href);
  assert.equal(url.origin, 'https://business.test');
  assert.equal(url.pathname, '/');
  assert.equal(url.searchParams.get('legendEdit'), 'signed-scope-a');
  assert.equal(f.calls.filter(call => call.path === '/profile/edit').length, 0);
  f.dom.window.close();
});

for (const scope of ['business', 'agent']) test(`${scope} blocked saved draft offers scoped recovery review without a write`, async () => {
  const f = await fixture({ scope, handoff: true, manageFailure: { status: 409,
    details: { error: 'website_draft_duplicate_identity', message: 'The saved draft requires recovery review.' } } });
  const link = [...f.document.querySelectorAll('a')].find(a => a.textContent.includes('Review saved website recovery'));
  assert.ok(link);
  const url = new URL(link.href);
  assert.equal(url.origin, scope === 'business' ? 'https://client.mylegnd.com' : 'https://business.test');
  assert.equal(url.pathname, scope === 'business' ? '/profile/edit' : '/');
  assert.equal(url.searchParams.get('legendEdit'), 'signed-opaque-state-1');
  assert.match(f.document.querySelector('[role=status]').textContent, /recovery review/);
  assert.equal(f.calls.filter(call => call.options.method === 'POST').length, 1); // Authorization exchange only.
  assert.equal(f.calls.some(call => /recover|publish|drafts/.test(call.path)), false);
  assert.equal(f.document.querySelector('.wm-grid'), null);
  f.dom.window.close();
});

test('unrelated startup conflicts and authorization failures do not offer draft recovery', async () => {
  for (const status of [401, 403, 409]) {
    const f = await fixture({ manageFailure: { status,
      details: { error: status === 409 ? 'revision_conflict' : 'website_draft_duplicate_identity', message: 'Request refused.' } } });
    assert.equal(f.document.querySelector('a'), null);
    assert.match(f.document.querySelector('[role=status]').textContent, /Request refused/);
    f.dom.window.close();
  }
});

test('agent scope excludes business domains and inbox; empty readiness is explicit', async () => {
  const f = await fixture({ scope: 'agent' });
  assert.equal([...f.document.querySelectorAll('button')].some(n => /^(Domains|Inquiries)/.test(n.textContent)), false);
  await f.click('Readiness'); assert.match(f.document.body.textContent, /No readiness checks are available/); f.dom.window.close();
});


test('business details edits public facts through canonical API without ownership fields', async () => {
  const f = await fixture(); await f.click('Business details');
  assert.equal(f.document.querySelector('input[type=email]').value, 'contact@business.test');
  f.document.querySelector('input[type=email]').value = 'new@business.test'; await f.click('Save business details');
  const body = f.calls.find(c => c.path.endsWith('/business-details') && c.body).body;
  assert.equal(body.expectedRevision, 7); assert.equal(body.details.contactEmail, 'new@business.test');
  assert.deepEqual(Object.keys(body.details).sort(), ['contactEmail', 'hours', 'locations', 'phone', 'services']);
  assert.equal(body.ownerEmail, undefined); f.dom.window.close();
});

test('usage displays measured storage and counts without inferred pricing', async () => {
  const f = await fixture(); f.state.usage = { mediaBytes: 1048576, mediaCount: 2, publishedVersions: 3, importedPages: 4 };
  f.document.querySelector('dialog').close();
  f.document.querySelector('[data-website-manage]').click(); await flush();
  await f.click('Usage'); assert.match(f.document.body.textContent, /1.00 MB/); assert.match(f.document.body.textContent, /Saved published versions/); f.dom.window.close();
});


test('named business draft opens the editor with the same authorized scope', async () => {
  const f = await fixture();
  f.state.drafts = [{ id: 'draft-a', name: 'Variant A', updatedUtc: '2026-09-23T10:00:00Z' }];
  f.document.querySelector('dialog').close();
  f.document.querySelector('[data-website-manage]').click(); await flush();
  await f.click('Drafts');
  await f.click('Edit');
  const load = f.calls.find(call => call.path.endsWith('/drafts/load'));
  assert.equal(load.body.draftId, 'draft-a');
  assert.equal(load.body.ticket, 'signed-scope-a');
  assert.match(source,/location\.href=editorHref\(\)/);
  assert.doesNotMatch(source,/section\('Draft loaded'\)/);
  assert.doesNotMatch(source,/Edit this draft/);
  f.dom.window.close();
});


test('workspace overview is compact icon navigation without repetitive action subtitles', async () => {
  const f = await fixture();
  for (const label of ['Drafts','Publish','Schedule','History','Readiness','Usage','Export','Promote This'])
    assert.ok([...f.document.querySelectorAll('.wm-tile')].some(node => node.textContent.trim() === label), label);
  assert.equal(f.document.querySelectorAll('.wm-tile small').length, 0);
  assert.ok(f.document.querySelectorAll('.wm-tile .wm-icon svg').length >= 8);
  assert.doesNotMatch(source,/Choose, edit, or delete named website variations/);
  assert.doesNotMatch(source,/Review and make your saved draft live/);
  f.dom.window.close();
});

test('draft workspace is one scrollable list with edit and delete actions in the same view', async () => {
  const f = await fixture();
  f.state.drafts = [
    { id:'draft-a', name:'Variant A', updatedUtc:'2026-09-23T10:00:00Z' },
    { id:'draft-b', name:'Variant B', updatedUtc:'2026-09-24T10:00:00Z' }
  ];
  // Fetch a new response after the simulated server draft list changes.
  f.document.querySelector('dialog').close();
  f.document.querySelector('[data-website-manage]').click(); await flush();
  await f.click('Drafts');
  assert.equal(f.document.querySelectorAll('.wm-draft-list .wm-list-row').length,2);
  assert.equal(f.document.querySelectorAll('.wm-draft-list .wm-list-actions').length,2);
  assert.equal([...f.document.querySelectorAll('.wm-draft-list button')].filter(node=>node.textContent==='Edit').length,2);
  assert.equal([...f.document.querySelectorAll('.wm-draft-list button')].filter(node=>node.textContent==='Delete').length,2);
  assert.equal([...f.document.querySelectorAll('button')].some(node=>node.textContent==='Load draft'),false);
  f.dom.window.close();
});


test('website workspace responsive CSS keeps mobile actions compact and draft rows usable',()=>{
  assert.match(managementCss,/\.wm-grid\{grid-template-columns:repeat\(2,minmax\(0,1fr\)\);gap:7px\}/);
  assert.match(managementCss,/\.wm-tile\{min-height:48px;padding:8px 9px;font-size:\.88rem\}/);
  assert.match(managementCss,/\.wm-list\{max-height:calc\(100dvh - 170px\)\}/);
  assert.match(managementCss,/\.wm-list-actions\{display:flex;gap:6px;flex:0 0 auto\}/);
  assert.equal(managementCss.includes('min-height:68px'),false);
  assert.equal(managementCss.includes('min-height:58px'),false);
});

test('Close marks pending startup dismissed before queued close event', async () => {
  const pending = deferred();
  const f = await fixture({ deferPath: '/profile/session', deferReply: pending });
  const dialog = f.document.querySelector('dialog');
  const call = f.calls.find(x => x.path === '/profile/session');
  assert.equal(dialog.querySelector('button').disabled, false);
  dialog.close = function () { this.open = false; }; // Defer the native close event.
  dialog.querySelector('button').click();
  assert.equal(call.options.signal.aborted, true);
  pending.release(); await flush();
  assert.equal(f.calls.some(x => x.path === '/api/website-content/manage'), false);
  assert.equal(dialog.querySelector('.wm-hero-actions'), null);
  dialog.dispatchEvent(new f.window.Event('close'));
  assert.equal(f.document.querySelector('dialog'), null);
  f.dom.window.close();
});

test('late management response cannot render after dismissal; new open obtains session', async () => {
  const pending = deferred();
  const f = await fixture({ deferPath: '/api/website-content/manage', deferReply: pending });
  const dialog = f.document.querySelector('dialog');
  const call = f.calls.find(x => x.path === '/api/website-content/manage');
  assert.equal(dialog.querySelector('button').disabled, false);
  dialog.querySelector('button').click();
  assert.equal(call.options.signal.aborted, true);
  pending.release(); await flush();
  assert.equal(dialog.querySelector('.wm-hero-actions'), null);
  assert.equal(f.document.querySelector('dialog'), null);
  f.document.querySelector('[data-website-manage]').click(); await flush();
  assert.ok(f.document.querySelector('dialog .wm-hero-actions'));
  assert.equal(f.calls.filter(x => x.path === '/profile/session').length, 2);
  assert.equal(f.calls.filter(x => x.path === '/api/website-content/manage').length, 2);
  f.dom.window.close();
});

test('one-time handoff dismissal ignores stale response; reopening uses fresh continuation', async () => {
  const pending = deferred();
  const f = await fixture({ handoff: true, deferPath: '/api/website-content/handoff', deferReply: pending });
  const first = f.calls.find(x => x.path === '/api/website-content/handoff');
  assert.ok(first);
  assert.equal(first.options.method, 'POST');
  assert.equal(first.body.state, 'opaque-state-1');
  f.document.querySelector('dialog button').click();
  assert.equal(first.options.signal.aborted, true);
  pending.release(); await flush();
  assert.equal(f.calls.filter(x => x.path === '/api/website-content/manage').length, 0);
  f.document.querySelector('[data-website-manage]').click(); await flush();
  const handoffs = f.calls.filter(x => x.path === '/api/website-content/handoff');
  assert.equal(handoffs.length, 2);
  assert.equal(handoffs[1].body.state, 'opaque-state-2');
  const reads = f.calls.filter(x => x.path === '/api/website-content/manage');
  assert.equal(reads.length, 1);
  assert.equal(reads[0].url.searchParams.get('ticket'), 'signed-opaque-state-2');
  assert.ok(f.document.querySelector('dialog .wm-hero-actions'));
  f.dom.window.close();
});

test('Escape cancel rejects pending startup before close event is delivered', async () => {
  const pending = deferred();
  const f = await fixture({ deferPath: '/profile/session', deferReply: pending });
  const dialog = f.document.querySelector('dialog');
  const request = f.calls.find(x => x.path === '/profile/session');
  dialog.dispatchEvent(new f.window.Event('cancel', { cancelable: true }));
  assert.equal(request.options.signal.aborted, true);
  pending.release(); await flush();
  assert.equal(f.calls.some(x => x.path === '/api/website-content/manage'), false);
  dialog.close();
  assert.equal(f.document.querySelector('dialog'), null);
  f.dom.window.close();
});

test('publish continues to use existing busy and unabortable POST semantics', async () => {
  const pending = deferred();
  const f = await fixture({ deferPath: '/api/website-content/manage/publish', deferReply: pending });
  const button = [...f.document.querySelectorAll('button')].find(x => x.textContent === 'Publish');
  assert.ok(button); button.click(); await flush();
  const post = f.calls.find(x => x.path === '/api/website-content/manage/publish');
  assert.ok(post);
  assert.equal(post.options.method, 'POST');
  assert.equal(post.options.signal, undefined);
  assert.equal(f.document.querySelector('dialog button').disabled, true);
  pending.release(); await flush();
  assert.equal(f.document.querySelector('dialog button').disabled, false);
  f.dom.window.close();
});

test('POST acceptance alone stays pending until exact GET status is available', async () => {
  let statusUnavailable = true;
  const f = await fixture({ publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publication-status') && statusUnavailable)
      return { ok: false, status: 503, json: async () => ({ error: 'unavailable' }) };
  } });
  await f.click('Publish');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  assert.equal(f.pendingPublication().phase, 'pending');
  assert.doesNotMatch(f.document.querySelector('[role=status]').textContent, /recorded|Website published|verified live/i);
  statusUnavailable = false;
  await f.click('Check publish status');
  assert.equal(f.pendingPublication().phase, 'review');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  f.dom.window.close();
});

test('ambiguous publication keeps one UUID through fresh handoff and page bootstrap', async () => {
  const f = await fixture({ handoff: true, publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publish')) throw new TypeError('Failed to fetch');
  } });
  await f.click('Publish');
  const original = f.pendingPublication();
  f.document.querySelector('dialog').close();
  f.document.querySelector('[data-website-manage]').click(); await flush();
  await f.click('Check publish status');
  assert.deepEqual(f.pendingPublication(), original);
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  const reads = f.calls.filter(x => x.path.endsWith('/publication-status'));
  assert.ok(reads.length >= 3);
  assert.ok(reads.every(x => x.url.searchParams.get('requestId') === original.requestId
    && x.url.searchParams.get('expectedRevision') === '7' && x.options.method === undefined));
  assert.equal(reads.at(-1).url.searchParams.get('ticket'), 'signed-opaque-state-2');
  const retained = f.storage(); f.dom.window.close();
  const resumed = await fixture({ storageEntries: retained });
  assert.deepEqual(resumed.pendingPublication(), original);
  assert.equal(resumed.calls.filter(x => x.path.endsWith('/publish')).length, 0);
  assert.equal(resumed.calls.find(x => x.path.endsWith('/publication-status')).url.searchParams.get('requestId'), original.requestId);
  resumed.dom.window.close();
});

test('bound absence proof settles original but requires fresh review before a new UUID', async () => {
  let allowPost = false, reviewPrompt = '';
  const f = await fixture({ publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publish') && !allowPost) throw new TypeError('Failed to fetch');
  } });
  await f.click('Publish');
  const original = f.pendingPublication();
  f.state.revision = 8;
  await f.click('Check publish status');
  assert.equal(f.pendingPublication().phase, 'review');
  assert.equal(f.pendingPublication().outcome, 'rejected');
  assert.equal(f.pendingPublication().requestId, original.requestId);
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  f.window.confirm = text => { reviewPrompt = text; return false; };
  const readsBefore = f.calls.filter(x => x.path === '/api/website-content/manage').length;
  await f.click('Publish');
  assert.ok(f.calls.filter(x => x.path === '/api/website-content/manage').length > readsBefore);
  assert.match(reviewPrompt, /reviewed the current saved draft/);
  assert.equal(f.pendingPublication().requestId, original.requestId);
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  allowPost = true; f.window.confirm = () => true;
  await f.click('Publish');
  const next = f.calls.filter(x => x.path.endsWith('/publish')).at(-1).body;
  assert.notEqual(next.requestId, original.requestId);
  assert.equal(next.expectedRevision, 8);
  f.dom.window.close();
});

for (const mismatch of ['request', 'revision']) {
  test(`mismatched ${mismatch} rejection never releases ambiguous publication`, async () => {
    const f = await fixture({ publicationBehavior: ({ parsed, body, state }) => {
      const requestId = body?.requestId ?? parsed.searchParams.get('requestId');
      const expectedRevision = body?.expectedRevision ?? Number(parsed.searchParams.get('expectedRevision'));
      const rejection = { disposition: 'rejected_before_commit',
        requestId: mismatch === 'request' ? 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' : requestId,
        expectedRevision: mismatch === 'revision' ? expectedRevision + 1 : expectedRevision, currentRevision: 8 };
      state.revision = 8;
      if (parsed.pathname.endsWith('/publish'))
        return { ok: false, status: 409, json: async () => ({ error: 'revision_conflict', publicationRejection: rejection }) };
      return { ok: true, status: 200, json: async () => ({ publicationScopeId: state.publicationScopeId, requestId, expectedRevision, found: false,
        publication: null, revision: 8, isCurrent: false, publicationRejection: rejection }) };
    } });
    await f.click('Publish');
    const original = f.pendingPublication();
    await f.click('Check publish status');
    assert.equal(f.pendingPublication().phase, 'pending');
    assert.equal(f.pendingPublication().requestId, original.requestId);
    assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
    f.dom.window.close();
  });
}

test('publication recovery for another canonical state cannot enter this scope', async () => {
  const first = await fixture({ publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publish')) throw new TypeError('Failed to fetch');
  } });
  await first.click('Publish');
  const retained = first.storage(), original = first.pendingPublication(); first.dom.window.close();
  const second = await fixture({ storageEntries: retained, publicationScopeId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' });
  assert.equal(second.calls.filter(x => x.path.endsWith('/publication-status')).length, 0);
  await second.click('Publish');
  assert.notEqual(second.pendingPublication().requestId, original.requestId);
  assert.ok(second.storage().some(([key, value]) => key === retained[0][0] && value === retained[0][1]));
  second.dom.window.close();
});

test('failed durable browser storage stops publication before POST', async () => {
  const f = await fixture();
  f.window.Storage.prototype.setItem = () => { throw new Error('Storage unavailable'); };
  await f.click('Publish');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 0);
  assert.match(f.document.querySelector('[role=status]').textContent, /retain publication recovery/);
  f.dom.window.close();
});

test('dismissed pending POST remains unabortable and late response cannot replace reopened UI', async () => {
  const pending = deferred();
  const f = await fixture({ deferPath: '/api/website-content/manage/publish', deferReply: pending });
  const button = [...f.document.querySelectorAll('button')].find(x => x.textContent === 'Publish');
  button.click(); await flush();
  const original = f.pendingPublication();
  const post = f.calls.find(x => x.path.endsWith('/publish'));
  f.document.querySelector('dialog').close();
  f.document.querySelector('[data-website-manage]').click(); await flush();
  const readsBefore = f.calls.filter(x => x.path.endsWith('/publication-status')).length;
  pending.release(); await flush();
  assert.equal(post.options.signal, undefined);
  assert.equal(f.calls.filter(x => x.path.endsWith('/publication-status')).length, readsBefore);
  assert.equal(f.pendingPublication().requestId, original.requestId);
  assert.equal(f.pendingPublication().phase, 'pending');
  assert.doesNotMatch(f.document.querySelector('[role=status]').textContent, /Publication recorded/);
  await f.click('Check publish status');
  assert.equal(f.pendingPublication().phase, 'review');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  f.dom.window.close();
});

test('committed POST response loss settles original from status without a second POST', async () => {
  const f = await fixture({ publicationBehavior: ({ parsed, body, state, publications }) => {
    if (parsed.pathname.endsWith('/publish')) {
      state.revision = body.expectedRevision + 1; state.publishedRevision = state.revision;
      publications.set(body.requestId, { versionId: body.requestId, publishedRevision: state.revision });
      throw new TypeError('Failed to fetch after commit');
    }
  } });
  await f.click('Publish');
  assert.equal(f.pendingPublication().phase, 'review');
  assert.equal(f.pendingPublication().outcome, 'published');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  assert.equal(f.publications.size, 1);
  assert.ok(![...f.document.querySelectorAll('button')].some(x => x.textContent === 'Resume original request'));
  f.dom.window.close();
});

test('proved equal-revision absence permits explicit resume using the original UUID only', async () => {
  let entered = false;
  const f = await fixture({ publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publish') && !entered) { entered = true; throw new TypeError('Failed to fetch before commit'); }
  } });
  await f.click('Publish');
  const original = f.pendingPublication();
  assert.equal(original.phase, 'pending');
  assert.equal(f.publications.size, 0);
  f.window.confirm = () => false;
  await f.click('Resume original request');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  f.window.confirm = () => true;
  await f.click('Resume original request');
  const posts = f.calls.filter(x => x.path.endsWith('/publish'));
  assert.equal(posts.length, 2);
  assert.deepEqual(posts[1].body, posts[0].body);
  assert.equal(posts[1].body.requestId, original.requestId);
  assert.equal(posts[1].body.expectedRevision, original.expectedRevision);
  assert.equal(f.pendingPublication().outcome, 'published');
  assert.equal(f.publications.size, 1);
  f.dom.window.close();
});

test('publication journal readback mismatch prevents entry into POST', async () => {
  const f = await fixture();
  const original = f.window.Storage.prototype.setItem;
  f.window.Storage.prototype.setItem = function (key, value) { original.call(this, key, value + ' '); };
  await f.click('Publish');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 0);
  assert.match(f.document.querySelector('[role=status]').textContent, /retain publication recovery/);
  f.dom.window.close();
});

test('journal replacement during status GET cannot authorize POST for another request', async () => {
  let replaceJournal = false, f;
  const replacementId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
  f = await fixture({ publicationBehavior: ({ parsed, state }) => {
    if (parsed.pathname.endsWith('/publish')) throw new TypeError('Failed to fetch before commit');
    if (parsed.pathname.endsWith('/publication-status') && replaceJournal) {
      replaceJournal = false;
      const requestId = parsed.searchParams.get('requestId'), expectedRevision = Number(parsed.searchParams.get('expectedRevision'));
      const key = f.storage().find(([key]) => key.startsWith('legend-website-publication-v1:'))[0];
      f.window.sessionStorage.setItem(key, JSON.stringify({ schemaVersion: 1, requestId: replacementId, expectedRevision, phase: 'pending' }));
      return { ok: true, status: 200, json: async () => ({ publicationScopeId: state.publicationScopeId,
        requestId, expectedRevision, found: false, publication: null, isCurrent: false, revision: expectedRevision }) };
    }
  } });
  await f.click('Publish');
  replaceJournal = true;
  await f.click('Resume original request');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  assert.equal(f.pendingPublication().requestId, replacementId);
  assert.equal(f.pendingPublication().phase, 'pending');
  f.dom.window.close();
});

test('status proof for a different canonical state remains pending without resume admission', async () => {
  const f = await fixture({ publicationBehavior: ({ parsed }) => {
    if (parsed.pathname.endsWith('/publish')) throw new TypeError('Failed to fetch');
    if (parsed.pathname.endsWith('/publication-status')) return { ok: true, status: 200, json: async () => ({
      publicationScopeId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      requestId: parsed.searchParams.get('requestId'), expectedRevision: Number(parsed.searchParams.get('expectedRevision')),
      found: false, publication: null, isCurrent: false, revision: Number(parsed.searchParams.get('expectedRevision')) }) };
  } });
  await f.click('Publish');
  assert.equal(f.pendingPublication().phase, 'pending');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 1);
  assert.ok(![...f.document.querySelectorAll('button')].some(x => x.textContent === 'Resume original request'));
  f.dom.window.close();
});
