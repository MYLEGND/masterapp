import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const file = path => readFileSync(new URL('../../' + path, import.meta.url), 'utf8');
const markup = file('SHARED/Views/Messaging/_CommandCenter.cshtml');
const css = file('SHARED/wwwroot/css/dashboard-home-shared.css');
const js = file('SHARED/wwwroot/js/messaging.js');
const sheet = file('SHARED/wwwroot/js/legend-modal.js');
const ai = file('AgentPortal/Views/Shared/_LegendFounderAiModal.cshtml');

test('Messages and AI consume one LEGEND mobile sheet and conversation shell contract', () => {
  for (const text of [markup, ai]) {
    assert.match(text, /legend-conversation-shell/);
    assert.match(text, /data-legend-mobile-sheet/);
    assert.match(text, /data-legend-sheet-snap="full"/);
  }
  assert.match(markup, /data-legend-modal-panel/);
  assert.match(markup, /id="messagingConversationSidebar"/);
  assert.match(markup, /id="messagingMobileMenu"/);
  assert.match(markup, /id="messagingSidebarScrim"/);
  assert.match(markup, /id="messagingMessages" data-legend-sheet-scroll/);
  assert.match(sheet, /function registerMobileSheet\(sheet\)/);
  assert.match(sheet, /function syncMobileSheetViewport\(\)/);
  assert.match(sheet, /visualViewport\?\.addEventListener\("resize", scheduleViewportOffsets/);
  assert.match(sheet, /visualViewport\?\.addEventListener\("scroll", scheduleViewportOffsets/);
});

test('no mirrored controls, changed messaging endpoints or service handlers', () => {
  for (const id of [
    'messagingCommandCenterClose', 'messagingCommandCenterUnread',
    'messagingChooseVoiceCall', 'messagingChooseVideoCall',
    'messagingJourneyCirclesOpen', 'messagingThreadContent',
    'messagingSendForm', 'messagingMessageBody', 'messagingSendButton',
    'messagingFiles', 'messagingConversationList'
  ]) {
    const count = markup.split('id="' + id + '"').length - 1;
    assert.equal(count, 1, 'Missing or duplicated control: ' + id);
  }
  assert.doesNotMatch(markup, /messaging-command-center-header-actions/);
  assert.match(js, /elements\.window\.classList\.add\('open'\)/);
  assert.match(js, /elements\.window\.classList\.remove\('open'\)/);
  assert.match(js, /setConversationSidebarOpen\(false\)/);
  assert.match(js, /selectConversationForCurrentIntent\(conversation\)/);
  assert.match(js, /#messagingSendForm/);
});

test('old independent mobile grid and forced-height override removed at their origin', () => {
  assert.doesNotMatch(css, /grid-template-rows:\s*minmax\(180px,\s*0\.42fr\)/);
  assert.doesNotMatch(css, /grid-template-rows:\s*minmax\(120px,\s*0\.24fr\)/);
  assert.doesNotMatch(css, /\.messaging-command-center-window\[data-legend-modal-panel\]\s*{\s*height:\s*100dvh\s*!important/);
  assert.match(css, /\.messaging-command-center-modal\.is-sidebar-open \.messaging-command-center-sidebar/);
  assert.match(css, /\.messaging-command-center-grid\s*{\s*position:\s*relative;\s*display:\s*block/);
  assert.match(css, /\.messaging-messages\s*{\s*min-height:\s*0/);
  assert.match(css, /\.messaging-send-form\s*{\s*flex:\s*0 0 auto;/);
  assert.match(css, /:not\(\.legend-conversation-shell\) :is\(button, \.btn, a\.btn\)/);
});
