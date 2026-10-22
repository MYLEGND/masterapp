import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../Protect-Website/Views/Quote/Life.cshtml', import.meta.url), 'utf8');
// Execute the shipped helpers and Back binding. Only their external analytics,
// persistence, timer and layout dependencies are controlled by this fixture.
function shippedFunction(name) {
  const match = source.match(new RegExp(`^  function ${name}\\([^]*?^  }`, 'm'));
  if (!match && ['isBackFocusTargetAvailable', 'getMicroStepBackFocusTarget'].includes(name)) return '';
  assert.ok(match, `Missing shipped helper ${name}`);
  return match[0];
}
const backBinding = source.slice(source.indexOf("  document.querySelectorAll('[data-step1-back]').forEach"), source.indexOf("  step1ContinueBtn?.addEventListener('click'"));
assert.ok(backBinding.includes('nativeMicroNeighbor'));
const helperNames = ['nativeMicroOrder', 'nativeMicroNeighbor', 'clearAutoAdvanceTimer', 'setStepTransitionState', 'getStepProgressLabel', 'getNearestFunnelPanel', 'getPanelFocusTarget', 'resolveScrollTarget', 'getScrollTopOffset', 'ensureFocusableTarget', 'spotlightPanel', 'scrollPanelIntoView', 'scrollIntoViewAndFocus', 'getFunnelPanelAlignment', 'focusFunnelPanel', 'isBackFocusTargetAvailable', 'getMicroStepBackFocusTarget', 'showMicroStep', 'markSelectedCardAdvancing', 'queueMicroAdvance'];

function fixture({ shell = 'public', order = [0, 1, 2, 3, 4], width = 1180, canonicalOrder = false } = {}) {
  const hiddenProgressCss = source.match(/body:not\(\.landing-mode\) \.lq-step1-panel \.lq-step-progress-wrap\{[^}]+\}/)[0];
  const landingProgressCss = source.match(/body\.life-landing \.lq-step1-panel \.lq-step-progress-wrap\{\s*display:none !important;\s*\}/)[0];
  const steps = order.map(i => `<section class="lq-micro-step" data-micro-step="${i}" ${i ? 'hidden' : ''}>
    <button type="button" data-step1-back="${i}" data-cms-step-back ${i === order[0] ? 'hidden' : ''}>Back</button>
    <div class="lq-step-question" id="question-${i}">Question ${i}</div>
    <label class="lq-card"><input id="answer-${i}" type="radio" name="answer-${i}" value="kept-${i}" checked>Answer</label>
    <input type="hidden" name="consent-${i}" value="unchanged">
  </section>`).join('');
  const dom = new JSDOM(`<!doctype html><style>${hiddenProgressCss}${landingProgressCss}</style>
    <body class="${shell === 'landing' ? 'landing-mode life-landing' : ''}"><header><nav class="navbar"></nav></header>
    ${shell === 'landing' ? '<section id="lifeEducationContactPanel"><div class="lq-contact-first-flow-surface">' : ''}
    <form id="lifeWizardForm"><div id="lifeStep1Panel" class="lq-step-panel lq-step1-panel ${shell === 'landing' ? 'lq-contact-first-wizard-panel' : ''}"><div class="lq-step-panel-pad">
    <div class="lq-step-progress-wrap"><div id="lifeStep1Progress">Question 1</div><span id="lifeStep1ProgressFill"></span></div>${steps}</div></div>
    <div id="lifeProcessingPanel" hidden><h2>Processing</h2></div><div id="lifeStep2Panel" hidden><h2>Contact</h2></div></form>
    ${shell === 'landing' ? '</div></section>' : ''}</body>`, { runScripts: 'outside-only', pretendToBeVisual: true });
  const { window } = dom;
  const { document } = window;
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: 800 });
  window.matchMedia = () => ({ matches: width < 768 });
  window.scrolls = [];
  window.scrollTo = options => window.scrolls.push(options);
  window.focusCalls = [];
  const originalFocus = window.HTMLElement.prototype.focus;
  window.HTMLElement.prototype.focus = function (options) { window.focusCalls.push({ id: this.id, options }); originalFocus.call(this, options); };
  // jsdom has no layout engine. The separate real-browser check uses real rects.
  if (!window.__lifeRealLayout) window.HTMLElement.prototype.getClientRects = function () {
    for (let el = this; el instanceof window.HTMLElement; el = el.parentElement) {
      if (el.hidden || window.getComputedStyle(el).display === 'none') return [];
    }
    return this.isConnected ? [{ width: 400, height: 40 }] : [];
  };
  document.querySelector('.navbar').getBoundingClientRect = () => ({ height: 80 });
  document.querySelector('#lifeStep1Panel').getBoundingClientRect = () => ({ top: 400, height: 300 });
  if (canonicalOrder) window.LEGEND_NATIVE_FORM_STEPS = { order: steps => order.map(i => steps[i]) };
  let now = 0, nextTimer = 0;
  const timers = new Map();
  window.setTimeout = (fn, delay = 0) => { const id = ++nextTimer; timers.set(id, { fn, at: now + delay }); return id; };
  window.clearTimeout = id => timers.delete(id);
  const tick = (duration = 2000) => {
    const until = now + duration;
    for (;;) {
      const next = [...timers].filter(([, t]) => t.at <= until).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
      if (!next) break;
      now = next[1].at; timers.delete(next[0]); next[1].fn();
    }
    now = until;
  };
  window.eval(`
    const form = document.getElementById('lifeWizardForm');
    const step1Panel = document.getElementById('lifeStep1Panel');
    const step1Progress = document.getElementById('lifeStep1Progress');
    const step1ProgressFill = document.getElementById('lifeStep1ProgressFill');
    const step2Panel = document.getElementById('lifeStep2Panel');
    const processingPanel = document.getElementById('lifeProcessingPanel');
    const step2Heading = step2Panel.querySelector('h2'), processingHeading = processingPanel.querySelector('h2');
    const contactFirstPanel = document.getElementById('lifeEducationContactPanel');
    const isPaidLandingExperience = ${shell === 'landing'}, isLandingMobile = ${shell === 'landing' && width < 768};
    const microSteps = Array.from(document.querySelectorAll('[data-micro-step]')).sort((a,b) => Number(a.dataset.microStep)-Number(b.dataset.microStep));
    const discoveryStepSequence = ['CoverageGoal','ProtectingWho','CoverageAmountOption','Age','TobaccoUse'];
    const microStepViewEvents = ['view-0','view-1','view-2','view-3','view-4'];
    let currentMicroStep = 0, microStepFocusVersion = 0, autoAdvanceTimer = null, autoCompletingDiscovery = false;
    const step1ContinueBtn = null;
    window.events = []; window.saved = 0;
    const metaSignalApi = { trackBacktrack: data => window.events.push({type:'back', ...data}) };
    function trackEvent(name, data) { window.events.push({type:'view', name, ...data}); }
    function persistFunnelState() { window.saved++; }
    ${helperNames.map(shippedFunction).join('\n')}
    ${backBinding}
    window.life = { show: showMicroStep, queue: queueMicroAdvance, current: () => currentMicroStep,
      panel: step1Panel, steps: microSteps, forwardFocus: () => focusFunnelPanel(step2Panel), processingFocus: () => focusFunnelPanel(processingPanel) };
  `);
  const life = window.life;
  const reset = index => { life.show(index, false, { trackView: false }); window.events.length = 0; window.saved = 0; };
  const back = () => life.steps[life.current()].querySelector('[data-step1-back]').click();
  return { dom, window, document, life, tick, reset, back };
}

for (const shell of ['public', 'landing']) {
  for (const order of [[0, 1, 2, 3, 4], [3, 1, 4, 0, 2]]) {
    for (let position = 1; position <= 4; position++) {
      test(`${shell}: Back ${position}, order ${order.join('')}, retains answers and one event pair`, () => {
        const f = fixture({ shell, order, canonicalOrder: shell === 'landing' });
        const from = order[position], to = order[position - 1];
        f.reset(from);
        const controls = Array.from(f.document.querySelectorAll('input'));
        const values = controls.map(el => [el.value, el.checked]);
        f.life.queue(from);
        f.back(); f.tick();
        assert.equal(f.life.current(), to);
        assert.equal(f.document.activeElement.id, `question-${to}`);
        assert.equal(f.document.activeElement.getAttribute('tabindex'), '-1');
        assert.deepEqual(Array.from(f.window.events, e => e.type), ['back', 'view']);
        assert.equal(f.window.events[0].toStep, `micro_${to + 1}`);
        assert.equal(f.window.events[1].microStep, to + 1);
        assert.equal(f.window.saved, 1);
        assert.ok(controls.every((el, index) => el === f.document.querySelectorAll('input')[index]));
        assert.deepEqual(controls.map(el => [el.value, el.checked]), values);
        assert.equal(f.window.focusCalls.length, 1);
        assert.equal(f.window.focusCalls[0].options.preventScroll, true);
        assert.equal(f.document.querySelector('.lq-step-progress-wrap').getClientRects().length, 0);
        f.dom.window.close();
      });
    }
  }
}

test('Back fallback ignores hidden/disabled controls and preserves existing tabindex', () => {
  const f = fixture(); f.reset(2);
  const destination = f.life.steps[1];
  destination.querySelector('.lq-step-question').hidden = true;
  destination.querySelector('input[type=radio]').disabled = true;
  destination.insertAdjacentHTML('beforeend', '<button id="hidden-choice" style="visibility:hidden">Hidden</button><button id="disabled-choice" aria-disabled="true">Unavailable</button><button id="visible-choice" tabindex="0">Choose</button>');
  f.back(); f.tick();
  assert.equal(f.document.activeElement.id, 'visible-choice');
  assert.equal(f.document.activeElement.getAttribute('tabindex'), '0');
  f.dom.window.close();
});

test('Back without an eligible destination never focuses hidden progress or Back', () => {
  const f = fixture(); f.reset(2);
  f.life.steps[1].querySelector('.lq-step-question').hidden = true;
  f.life.steps[1].querySelector('input[type=radio]').disabled = true;
  f.back(); f.tick();
  assert.equal(f.window.focusCalls.length, 0);
  assert.equal(f.window.scrolls.length, 1);
  f.dom.window.close();
});

for (const delay of [0, 50]) {
  for (const mutation of ['advance', 'return-same', 'detach', 'hide', 'inert', 'aria-hidden', 'panel-hidden']) {
    test(`Back rejects stale/hidden target after ${delay}ms: ${mutation}`, () => {
      const f = fixture(); f.reset(3); f.back(); f.tick(delay);
      const target = f.document.querySelector('#question-2');
      if (mutation === 'advance') f.life.show(3, false);
      if (mutation === 'return-same') { f.life.show(3, false); f.life.show(2, false); }
      if (mutation === 'detach') target.remove();
      if (mutation === 'hide') target.style.display = 'none';
      if (mutation === 'inert') f.life.steps[2].setAttribute('inert', '');
      if (mutation === 'aria-hidden') { target.setAttribute('aria-hidden', 'true'); }
      if (mutation === 'panel-hidden') f.life.panel.hidden = true;
      f.tick();
      assert.equal(f.window.focusCalls.length, 0);
      if (delay === 0) assert.equal(f.window.scrolls.length, 0);
      f.dom.window.close();
    });
  }
}

test('Rapid Back twice focuses only the latest destination', () => {
  const f = fixture(); f.reset(4); f.back(); f.tick(50); f.back(); f.tick();
  assert.equal(f.life.current(), 2);
  assert.equal(f.document.activeElement.id, 'question-2');
  assert.deepEqual(f.window.focusCalls.map(c => c.id), ['question-2']);
  assert.deepEqual(Array.from(f.window.events, e => e.type), ['back', 'view', 'back', 'view']);
  f.dom.window.close();
});

test('First canonical step keeps its hidden Back and no-op policy', () => {
  const f = fixture({ order: [3, 0, 2, 4, 1] }); f.reset(3);
  assert.equal(f.life.steps[3].querySelector('[data-step1-back]').hidden, true);
  f.back(); f.tick();
  assert.equal(f.life.current(), 3);
  assert.equal(f.window.events.length, 0);
  assert.equal(f.window.focusCalls.length, 0);
  f.dom.window.close();
});

for (const shell of ['public', 'landing']) for (const width of [390, 1180]) {
  test(`${shell} ${width}: Back retains panel alignment, navbar offset and scroll behavior`, () => {
    const f = fixture({ shell, width }); f.reset(2); f.back(); f.tick();
    const offset = (shell === 'public' ? 80 : 0) + (width < 768 ? 10 : 18);
    const bottom = width < 768 ? 12 : 24;
    assert.equal(f.window.scrolls.length, 1);
    assert.equal(f.window.scrolls[0].top, 400 - offset - (800 - offset - bottom - 300) / 2);
    assert.equal(f.window.scrolls[0].behavior, shell === 'landing' && width < 768 ? 'auto' : 'smooth');
    f.dom.window.close();
  });
}

test('Forward, contact and processing keep their original default focus targets', () => {
  const f = fixture(); f.life.show(1, true); f.tick();
  assert.equal(f.window.focusCalls.at(-1).id, 'lifeStep1Progress');
  f.life.forwardFocus(); f.tick();
  assert.equal(f.window.focusCalls.at(-1).id, '');
  f.life.processingFocus(); f.tick();
  assert.equal(f.window.focusCalls.at(-1).id, '');
  assert.equal(f.window.focusCalls.length, 3);
  f.dom.window.close();
});


test('A fallback control disabled during the focus delay cannot receive focus', () => {
  const f = fixture(); f.reset(2);
  const destination = f.life.steps[1];
  destination.querySelector('.lq-step-question').hidden = true;
  const answer = destination.querySelector('input[type=radio]');
  f.back(); f.tick(50); answer.disabled = true; f.tick();
  assert.equal(f.window.focusCalls.length, 0);
  f.dom.window.close();
});

for (const tag of ['input', 'button', 'select', 'textarea']) {
  test(`Back preserves the natural Tab stop of an unannotated ${tag}`, () => {
    const f = fixture(); f.reset(2);
    const destination = f.life.steps[1];
    destination.querySelector('.lq-step-question').hidden = true;
    destination.querySelector('input[type=radio]').disabled = true;
    const control = f.document.createElement(tag);
    control.id = 'natural-control';
    if (tag === 'button') control.type = 'button';
    destination.append(control);
    const originalTabIndex = control.tabIndex;
    assert.equal(control.hasAttribute('tabindex'), false);
    f.back(); f.tick();
    assert.equal(f.document.activeElement, control);
    assert.equal(control.hasAttribute('tabindex'), false);
    assert.equal(control.tabIndex, originalTabIndex);
    assert.equal(control.tabIndex, 0);
    f.dom.window.close();
  });
}
