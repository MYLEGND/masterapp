import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/leads-index.js', import.meta.url), 'utf8');
const drawerSource = source.slice(source.indexOf('async function openDrawerForRow(row)'), source.indexOf('// ===== Production (Lead)'));
function fixture(fail = false) {
  const errors = [], requests = [], messages = [], opened = [];
  const node = () => ({ dataset: {}, value: '', textContent: '', checked: false,
    classList: { add(value) { opened.push(value); } }, setAttribute() {}, removeAttribute() {} });
  const context = { window: {}, console: { error() {} },
    quickViewDiagnostics: { log() {}, error(message, detail) { errors.push(detail.error || message); } },
    quickViewBusyCalendar: { refresh() {} },
    toast(message) { messages.push(message); },
    async loadQuickView(id) { assert.ok(opened.includes('open')); requests.push(id); if (fail) throw Error('detail unavailable'); return { firstName: 'Fixture', lastName: 'Lead' }; },
    norm: value => String(value || '').trim(), fullName: () => 'Fixture Lead',
    currentPipelineStage: () => 'LifeInsurance', normalizePipelineStageValue: value => value,
    normalizeContactStatusValue: value => value, stageAgeDays: () => 0,
    waitingLabel: value => value, todayISO: () => '2026-11-01',
    describeQuickViewRow: () => ({}), quickViewBindingSnapshot: () => ({}),
    inferMeetingType: () => '', rowLatestAppointment: () => null,
    loadSavedZoomLink: () => '' };
  for (const name of ['loadLeadActionsPanel','loadLeadCommitmentsPanel','syncDrawerEmailDisplay',
    'applyQuickViewContactProfileLabels','setDrawerNextActionDate','applyMeetingType','loadProductionHistory',
    'renderPortalActions','renderTimeline','renderMentionNotes','renderIntakeSnapshot','renderAppointmentSnapshot',
    'refreshLeadOverviewSummary','updateZoomControls','syncQuickViewDisclosures','closeAllMenus','storeRowLatestAppointment','hydrateRow']) context[name] = () => {};
  for (const name of new Set(drawerSource.match(/\bd[A-Z]\w*\b/g))) context[name] = node();
  for (const name of ['drawer','drawerBackdrop','btnMail','btnCall']) context[name] = node();
  vm.createContext(context);
  vm.runInContext('let drawerEditing=false, activeClientId=null, activeClientDetail=null;\n' + drawerSource, context);
  return { context, errors, requests, messages, open: () => context.openDrawerForRow({dataset:{clientId:'synthetic-lead',first:'Fixture',last:'Lead'}}) };
}
test('Quick View opens and hydrates details without the removed notes overlay', async () => {
  const f = fixture(); await f.open();
  assert.deepEqual(f.errors, []); assert.deepEqual(f.requests, ['synthetic-lead']);
  assert.equal(f.context.dSaved.textContent, 'Loaded');
  assert.equal(f.context.dName.textContent, 'Fixture Lead');
  assert.equal(vm.runInContext('activeClientId', f.context), 'synthetic-lead');
});
test('Quick View retains truthful detail failure reporting', async () => {
  const f = fixture(true); await f.open();
  assert.deepEqual(f.requests, ['synthetic-lead']);
  assert.equal(f.context.dSaved.textContent, 'Load failed');
  assert.ok(f.messages.includes('Failed to load lead details.'));
});
for (const page of ['leads', 'clients']) {
  test(`${page}: initial render can read canonical history reminders`, () => {
    const script = readFileSync(new URL(`../../AgentPortal/wwwroot/js/${page}-index.js`, import.meta.url), 'utf8');
    const declaration = 'let historyReminderItems = [];';
    const firstRender = script.indexOf(`safeStartupInit("${page === 'leads' ? 'Leads' : 'Clients'} initial pipeline render"`);
    const listStart = script.indexOf('function listDueItems(){');
    const listEnd = script.indexOf('function refreshRemindersUI(){');
    assert.equal(script.split(declaration).length - 1, 1);
    assert.ok(firstRender >= 0 && listStart >= 0 && listEnd > listStart);
    const list = script.slice(listStart, listEnd);
    // Preserve source execution order: the old order throws a real TDZ error.
    const before = script.indexOf(declaration) < firstRender;
    const execution = before ? declaration + '\nlistDueItems();' : 'listDueItems();\n' + declaration;
    assert.equal(vm.runInNewContext(list + '\n' + execution + '\nlistDueItems().length', { rows: [] }), 0);
  });
}
test('Lead notes retain the canonical shared history route', () => {
  assert.match(source, /window\.location\.assign\('\/CrmHistory\?kind=lead&id='/);
});
