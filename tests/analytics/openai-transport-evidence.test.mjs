import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
const render = source.slice(source.indexOf('    function renderEvidence(provider, connected, configured)'), source.indexOf("    renderEvidence('meta'"));
const severity = source.slice(source.indexOf('    const severityForOpenAi = () => {'), source.indexOf("    const openAiStatus =", source.indexOf('    const severityForOpenAi = () => {')));
const actionStart = source.indexOf("    const openAiAction = document.getElementById('marketing-setup-openai-action-status');");
const action = source.slice(actionStart, source.indexOf("    const disconnect =", actionStart));

function display(evidenceError = false) {
  const cells = Object.fromEntries(['connected', 'configured', 'attempted', 'accepted', 'attribution'].map(key => [key, { textContent: '' }]));
  const detail = { textContent: '' }, actionCell = { textContent: '', closest: () => ({}) };
  const row = { querySelector: selector => cells[selector.match(/"(.*?)"/)[1]], querySelectorAll: () => Object.values(cells) };
  const evidence = { openAi: { attempted: 2, accepted: 0, transportAcknowledged: 2, acceptanceEvidence: 'http_accepted' } };
  const context = { payload: { evidence, evidenceError }, evidence, openAi: { connected: true, accountStatus: 'active', reviewStatus: 'approved', pixelConfigured: true, conversionsApiConfigured: true }, openAiHealth: {}, applySeverity: () => {}, document: { getElementById: id => id.endsWith('receipt-evidence') ? detail : id.endsWith('action-status') ? actionCell : row } };
  vm.runInNewContext(render + severity + action + "\nrenderEvidence('openai', true, true);", context);
  return { cells, detail, actionCell };
}

test('OpenAI shows HTTP count as transport and keeps provider acceptance unverified', () => {
  const { cells, detail, actionCell } = display();
  assert.equal(cells.accepted.textContent, 'Unverified');
  assert.match(detail.textContent, /Transport acknowledged: 2/);
  assert.match(detail.textContent, /Provider acceptance unverified/);
  assert.equal(actionCell.textContent, 'Transport acknowledged · provider acceptance and downstream attribution unverified');
});

test('Unavailable evidence is never displayed as zero accepted events', () => {
  const { cells } = display(true);
  for (const cell of Object.values(cells)) assert.equal(cell.textContent, 'Unavailable');
});

const badgeSource = source.slice(source.indexOf('  function metaTrackingBadge('), source.indexOf('  // Maps each modal id', source.indexOf('  function metaTrackingBadge(')));
function metaBadge(state) {
  const context = { state, escapeHtml: value => String(value).replaceAll('<', '&lt;').replaceAll('>', '&gt;') };
  return vm.runInNewContext(badgeSource + '\nmetaTrackingBadge(state);', context);
}

test('Lead Snapshot renders authoritative delivery without converting missing evidence to pending', () => {
  for (const [status, label] of Object.entries({ provider_accepted: 'Meta accepted', blocked_human_evidence: 'Blocked',
    pending: 'Pending', retrying: 'Retrying', failed: 'Failed', unknown: 'Unverified', sent: 'Unverified',
    receipt_unverified: 'Unverified', transport_acknowledged: 'Transport only', not_configured: 'Not configured',
    skipped_not_dispatch_eligible: 'Not eligible', requires_reconciliation: 'Needs reconciliation', mixed: 'Mixed delivery' })) {
    const html = metaBadge({ serverCapiStatus: status, browserPixelSent: true, serverCapiSent: true, dedupReady: true });
    assert.ok(html.includes(label), `${status} should render ${label}`);
    if (status !== 'pending') assert.ok(!html.includes('>Pending<'));
  }
  assert.match(metaBadge({}), /Unverified/);
  assert.ok(!metaBadge({ serverCapiStatus: 'blocked_<script>' }).includes('<script>'));
});

const detailsStart = source.indexOf('  function openAiDeliveryDetails(');
const detailsSource = source.slice(detailsStart, source.indexOf('  function metaTrackingBadge(', detailsStart));
test('delivery details retain exact reason and retry identity without manufacturing scheduled retries', () => {
  const context = {};
  vm.runInNewContext(detailsSource, context);
  const row = { canonicalEventId: 'lead-synthetic', status: 'blocked_human_evidence', errorCode: 'human_evidence_reviewed_needed',
    analyticsEventId: 42, deliveryId: 'receipt', canonicalSource: 'AnalyticsEvent', attemptCount: 0, destinationBinding: 'current',
    advertiserAccountId: 'account', conversionDataSourceId: 'source', pixelId: 'pixel' };
  const text = context.openAiDeliveryDetails([row]);
  assert.match(text, /blocked_human_evidence/);
  assert.match(text, /human_evidence_reviewed_needed/);
  assert.match(text, /Next retry not scheduled/);
  assert.match(text, /Source AnalyticsEvent \/ 42/);
  assert.match(context.openAiDeliveryDetails([{ ...row, nextAttemptUtc: '2026-11-09T23:00:00Z' }]), /2026-11-09T23:00:00Z/);
  assert.equal(context.openAiDeliveryDetails(null), 'Delivery details unavailable');
  assert.match(context.openAiDeliveryDetails([]), /No delivery receipts/);
  assert.equal(context.openAiDeliveryDetails(Array.from({ length: 25 }, () => row)).split('Event lead-synthetic').length - 1, 20);
});
