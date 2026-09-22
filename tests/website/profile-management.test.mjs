import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../Legend-Design/legend-website-management.js', import.meta.url), 'utf8');
const flush = () => new Promise(resolve => setTimeout(resolve, 40));
async function fixture({ caps = {}, failPublish = false, scope = 'business' } = {}) {
  const dom = new JSDOM(`<button data-website-manage data-session="/profile/session" data-edit="/profile/edit" data-live="https://business.test" data-title="Business A" data-scope="${scope}">Manage</button>`, { url: 'https://client.mylegnd.com/profile', runScripts: 'outside-only' });
  const { window } = dom, calls = [];
  window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event('close')); };
  window.URL.createObjectURL = () => 'blob:export'; window.URL.revokeObjectURL = () => {};
  window.HTMLAnchorElement.prototype.click = function () {};
  const state = { revision: 7, publishedRevision: 6, document: { version: 1, elements: {} }, capabilities: { canPublish: true, canImport: true, canManageDomains: true, canSchedule: true, ...caps }, history: [{ versionId: 'version-a', revision: 6, createdUtc: '2026-09-01T10:00:00Z' }], readiness: { checks: [] } };
  window.fetch = async (url, options = {}) => {
    const parsed = new URL(url, window.location.href), body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path: parsed.pathname, url: parsed, options, body });
    let value = state;
    if (parsed.pathname === '/profile/session') value = { ticket: 'signed-scope-a', apiBase: 'https://protect.mylegnd.com' };
    if (parsed.pathname.endsWith('/publish') && failPublish) return { ok: false, status: 409, json: async () => ({ message: 'Draft changed elsewhere' }) };
    if (parsed.pathname.endsWith('/export')) value = { format: 'legend-website-v1', draft: { version: 1, elements: { a: { text: 'Owned content' } } } };
    if (parsed.pathname.endsWith('/import')) value = { report: { sourceUrl: body.sourceUrl, addedComponents: 2, preservedComponents: 4, warnings: ['Booking requires integration'], pages: [{ title: 'Services', url: 'https://old.test/services', forms: ['booking'], warnings: ['Review links'] }] } };
    if (parsed.pathname.endsWith('/business-details')) value = { revision: 7, details: { contactEmail: 'contact@business.test', phone: '123', hours: '9–5', services: 'Repairs', locations: 'Main street' } };
    if (parsed.pathname.endsWith('/domains')) value = options.method === 'POST'
      ? { cnameTarget: 'sites.example.test', binding: { id: 'binding-new', hostname: body.hostname, status: 'pending', certificateStatus: 'pending', verificationJson: JSON.stringify({ ownership: { type: 'TXT', name: '_verify.' + body.hostname, value: 'proof-new' } }) } }
      : { cnameTarget: 'sites.example.test', domains: [{ id: 'binding-a', hostname: 'business.test', status: 'pending', certificateStatus: 'pending', verificationJson: JSON.stringify({ ownership: { type: 'TXT', name: '_verify.business.test', value: 'proof-value' } }) }] };
    if (parsed.pathname === '/api/website-inquiries/manage') value = { inquiries: [{ id: 'inquiry-a', name: '<img src=x onerror=alert(1)>', email: 'a@example.test', message: 'Please call', status: 'New' }] };
    return { ok: true, status: 200, json: async () => value };
  };
  window.eval(source); window.document.querySelector('[data-website-manage]').click(); await flush();
  const click = async text => { const found = [...window.document.querySelectorAll('button')].find(node => node.textContent.startsWith(text)); assert.ok(found, `button ${text}`); found.click(); await flush(); };
  return { dom, window, document: window.document, calls, click, state };
}

test('profile session authorizes scope; publish requires confirmation and carries current revision', async () => {
  const f = await fixture();
  assert.equal(f.calls[0].options.credentials, 'same-origin');
  assert.equal(f.calls[1].url.searchParams.get('ticket'), 'signed-scope-a');
  assert.equal(f.calls[1].options.credentials, 'omit');
  await f.click('Publish draft');
  assert.equal(f.calls.filter(x => x.path.endsWith('/publish')).length, 0);
  await f.click('Publish draft');
  assert.deepEqual(f.calls.find(x => x.path.endsWith('/publish')).body, { ticket: 'signed-scope-a', expectedRevision: 7 });
  assert.match(f.document.querySelector('[role=status]').textContent, /published/); f.dom.window.close();
});

test('editor permissions hide owner-only operations while keeping draft editor', async () => {
  const f = await fixture({ caps: { canPublish: false, canImport: false, canManageDomains: false, canSchedule: false } });
  const editorLink = [...f.document.querySelectorAll('a')].find(a => new URL(a.href, f.window.location.href).pathname === '/profile/edit');
  assert.ok(editorLink);
  assert.equal(new URL(editorLink.href, f.window.location.href).searchParams.get('legendEdit'), 'signed-scope-a');
  for (const text of ['Publish draft', 'Domains', 'Schedule publication', 'Import content']) assert.equal([...f.document.querySelectorAll('button')].some(n => n.textContent.startsWith(text)), false);
  await f.click('Version history'); assert.equal([...f.document.querySelectorAll('button')].some(n => n.textContent === 'Restore'), false); f.dom.window.close();
});

test('revision conflict retains workspace and displays server error without false success', async () => {
  const f = await fixture({ failPublish: true }); await f.click('Publish draft'); await f.click('Publish draft');
  assert.equal(f.document.querySelector('[role=status]').textContent, 'Draft changed elsewhere'); assert.ok(f.document.querySelector('dialog').open); f.dom.window.close();
});

test('authorized import accepts own export draft and displays actionable migration report', async () => {
  const f = await fixture(); await f.click('Import content');
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
  const f = await fixture(); await f.click('Schedule publication');
  f.document.querySelector('input[type=datetime-local]').value = '2099-01-02T12:30'; await f.click('Schedule draft');
  const body = f.calls.find(c => c.path.endsWith('/schedule')).body; assert.equal(body.expectedRevision, 7); assert.match(body.publishUtc, /^2099-01-02T/); f.dom.window.close();
});

test('business inbox handles content as text and updates selected inquiry only', async () => {
  const f = await fixture(); await f.click('Inquiries'); assert.equal(f.document.querySelector('img'), null);
  f.document.querySelector('select').value = 'Contacted'; await f.click('Save status');
  assert.deepEqual(f.calls.find(c => c.path.endsWith('/manage/status')).body, { ticket: 'signed-scope-a', inquiryId: 'inquiry-a', status: 'Contacted' }); f.dom.window.close();
});

test('agent scope excludes business domains and inbox; empty readiness is explicit', async () => {
  const f = await fixture({ scope: 'agent' });
  assert.equal([...f.document.querySelectorAll('button')].some(n => /^(Domains|Inquiries)/.test(n.textContent)), false);
  await f.click('Launch readiness'); assert.match(f.document.body.textContent, /No readiness checks are available/); f.dom.window.close();
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
  await f.click('Website usage'); assert.match(f.document.body.textContent, /1.00 MB/); assert.match(f.document.body.textContent, /Published versions/); f.dom.window.close();
});
