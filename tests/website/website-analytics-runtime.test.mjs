import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
async function fixture({ device = true, founder = false, siteKey = 'legend', selectedAgent = false, business = false, global = false, performanceHandler = null, summaryHandler = null, healthHandler = null, captureDeadlines = false } = {}) {
  const founderLinks = { legend: 'https://legend.example.test/', protect: 'https://protect.example.test/' };
  const agentLink = 'https://protect.example.test/a/agent-one';
  const agents = founder ? [{ id: 'founder-profile', primaryUrl: founderLinks[siteKey] }, { id: 'agent-profile', primaryUrl: agentLink }] : [];
  const routes = [{ displayName: 'Life', basePath: '/quote/life', controlPath: '/quote/life', defaultPageVariant: 'landing', availableVariants: [{ variant: 'landing', isControl: true }] }];
  const dom = new JSDOM(`<!doctype html><div class="fa-shell" data-analytics-base="${business ? '/business/11111111-1111-1111-1111-111111111111/analytics' : '/WebsiteAnalytics'}" data-caller-profile-id="founder-profile" data-initial-scope-profile-id="${global ? '' : selectedAgent ? 'agent-profile' : 'founder-profile'}" data-initial-scope-label="Founder Personal"
    data-initial-site-key="${siteKey}" data-agent-options='${JSON.stringify(agents)}' data-founder-site-links='${JSON.stringify(founder ? founderLinks : {})}'
    data-personal-link="${founder ? founderLinks[siteKey] : agentLink}" data-landing-routes-base-url="https://protect.example.test/" data-landing-routes='${JSON.stringify(routes)}'></div>
    <span id="growth-base-link"></span><a id="growth-open-base"></a><button id="growth-copy-base"></button>
    <button class="wa-site-switch-btn" data-site-key="legend"></button><button class="wa-site-switch-btn" data-site-key="protect"></button>
    <div id="product-links-list"></div><span id="landing-routes-base-url"></span>
    <span id="fa-range-label"></span><span id="wa-scope-label"></span>
    <span id="mod-traffic-meta"></span><span id="mod-page-meta"></span><span id="mod-cta-meta"></span>
    <span id="kpi-intent-warning"></span><span id="kpi-session-warning"></span>
    <div id="traffic-quality-empty-state" hidden></div><span id="ai-drawer-scope-label"></span>
    <span id="mh-range-label"></span>
    <select id="wa-scope-select"><option value="">Global</option><option value="founder-profile">Founder Personal</option><option value="agent-profile">Agent</option></select>
    <button class="btn-range" data-range="7d"></button><button class="btn-range" data-range="30d"></button>
    <div id="kpi-pageviews"></div><div id="kpi-session"></div><span id="mh-score"></span><span id="mh-verdict"></span>
    <button id="analytics-refresh" data-analytics-refresh>Refresh</button><span id="analytics-refresh-status"></span>
    <button id="channel-performance-refresh" data-analytics-refresh>Refresh</button>
    <span id="growth-economics-overview"></span><span id="channel-performance-overview"></span>
    <div id="channelOutcomesModal"><div class="modal-body"><div id="channel-performance-grid"></div></div></div>
    <div id="growthEconomicsModal"><div class="modal-body"><div id="growth-economics-grid"></div><div id="growth-economics-spend"></div></div></div>
    <div id="behaviorModal"><div class="modal-body"><span id="bhvr-total-sessions"></span></div></div>
    <div id="convModal"><div class="modal-body"><button class="traffic-type-btn" data-type="paid">Ads</button><span id="conv-total"></span><span id="conv-intent"></span><span id="conv-session"></span><span id="conv-intent-sub"></span></div></div>
    <div id="advertisingCommandModal"><button id="advertising-command-refresh">Refresh</button><button id="advertising-command-promote">Promote</button><span id="advertising-command-status"></span><span id="advertising-command-connection"></span><span id="advertising-command-campaign-count"></span><span id="advertising-command-approval-count"></span><span id="advertising-command-audit-count"></span><div id="advertising-command-campaigns"></div><div id="advertising-command-proposals"></div></div>
    <textarea id="marketing-manager-goal">Find the next qualified lead</textarea><button id="marketing-manager-build"></button>
    <input type="hidden" name="__RequestVerificationToken" value="test-request-token" />
    ${device ? '<button id="mod-device-intelligence"></button><div id="deviceIntelligenceModal"></div><div id="deviceIntelligenceContent"></div><div id="deviceSessions"></div>' : ''}`,
    { url: 'https://portal.example.test/WebsiteAnalytics', runScripts: 'outside-only' });
  const { window } = dom;
  await new Promise(resolve => window.document.addEventListener('DOMContentLoaded', resolve, { once: true }));
  const calls = [], errors = [], polls = [], deadlines = [], requests = [];
  const pending = new Set();
  const track = promise => {
    const tracked = Promise.resolve(promise).finally(() => pending.delete(tracked));
    pending.add(tracked);
    return tracked;
  };
  window.setInterval = callback => { polls.push(callback); return polls.length; };
  const setTimeout = window.setTimeout.bind(window);
  window.setTimeout = (callback, delay, ...args) => {
    if (captureDeadlines && delay === 45000) { deadlines.push(callback); return -deadlines.length; }
    return setTimeout(callback, delay, ...args);
  };
  window.console.error = (...args) => errors.push(args.map(String).join(' '));
  window.console.warn = () => {};
  window.bootstrap = { Modal: { getOrCreateInstance: () => ({ show() {}, hide() {} }) } };
  const copied = [];
  window.navigator.clipboard = { writeText: value => { copied.push(value); return Promise.resolve(); } };
  const fetchFixture = async (url, options = {}) => {
    calls.push(String(url));
    requests.push({ url: String(url), ...options });
    const path = new URL(url, window.location.href).pathname;
    if (path.endsWith('/marketing-manager/performance') && performanceHandler) return performanceHandler(url, options);
    if (path.endsWith('/summary') && summaryHandler) return summaryHandler(url, options);
    if (path.endsWith('/marketing-health') && healthHandler) return healthHandler(url, options);
    const data = path.endsWith('/summary') ? { isAvailable: true, scopeLabel: 'Founder Personal', pageViews: 7, uniqueVisitors: 2, sessions: 2, verifiedLeads: 0, sessionConversionRate: 0 }
      : path.endsWith('/DeviceIntelligence') ? { sessions: 2, events: 7 }
      : path.endsWith('/conversions') ? { totalConversions: 2, recent: [] }
      : path.endsWith('/marketing-manager/performance') ? { channels: [], economics: { totalMarketingSpend: 12, channels: [] } }
      : { channels: [] };
    const response = new Response(JSON.stringify(data), { headers: { 'content-type': 'application/json' } });
    for (const method of ['json', 'text']) {
      const read = response[method].bind(response);
      response[method] = (...args) => track(read(...args));
    }
    return response;
  };
  window.fetch = (...args) => track(fetchFixture(...args));
  const dispose = async () => {
    let observedCalls;
    do {
      observedCalls = calls.length;
      await Promise.all([...pending]);
      // Fetch/JSON continuations can enqueue supporting requests. Drain the
      // current event-loop turn before deciding the fixture is idle.
      await new Promise(resolve => setImmediate(resolve));
    } while (pending.size || calls.length !== observedCalls);
    window.close();
  };
  window.eval(source);
  window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
  await new Promise(resolve => setTimeout(resolve, 30));
  return { dom, window, calls, errors, copied, polls, deadlines, requests, dispose };
}


async function providerFixture({ url = 'https://portal.example.test/WebsiteAnalytics/Index?agentProfileId=founder-profile&siteKey=legend', setupHandler = null, postHandler = null, googleSetup = null, connected = false } = {}) {
  const dom = new JSDOM(`<!doctype html>
    <div class="fa-shell" data-analytics-base="/WebsiteAnalytics" data-caller-profile-id="founder-profile"
      data-initial-scope-profile-id="founder-profile" data-initial-scope-label="Founder Personal"
      data-initial-site-key="legend" data-agent-options='[{"id":"founder-profile","name":"Founder"},{"id":"agent-profile","name":"Agent"}]'></div>
    <select id="wa-scope-select"></select>
    <div id="marketingSetupModal"><div id="marketing-setup-scope-label">Loading scoped setup…</div>
      <form id="marketing-setup-form">
        <section class="marketing-setup-section">
          <input id="marketing-setup-revision"><input id="marketing-setup-profile-revision">
          <input id="marketing-setup-pixel"><input id="marketing-setup-test-code">
          <a id="marketing-setup-meta-connect" aria-disabled="true">Meta</a>
          <a id="marketing-setup-calendar-connect" aria-disabled="true">Calendar</a>
          <button type="button" id="marketing-setup-calendar-disconnect">Disconnect calendar</button>
          <button type="button" id="marketing-setup-openai-verify">Verify ChatGPT Ads</button>
          <div id="marketing-setup-openai-connect-panel">
            <input id="marketing-setup-openai-api-key" type="password">
            <button type="button" id="marketing-setup-openai-connect-submit">Connect ChatGPT Ads</button>
            <button type="button" id="marketing-setup-openai-connect-cancel">Cancel</button>
          </div>
          <button type="button" id="marketing-setup-openai-disconnect">Disconnect ChatGPT Ads</button>
        </section>
        <div id="marketing-setup-status"></div>
        <button type="button" id="marketing-setup-retry" hidden>Retry</button>
        <button id="marketing-setup-save">Save</button>
        ${['google', 'tiktok'].map(provider => `
          <section class="marketing-setup-section">
            <div id="marketing-setup-${provider}-status"></div>
            <div id="marketing-setup-${provider}-account"></div>
            <div id="marketing-setup-${provider}-connection-setup" hidden></div>
            <a id="marketing-setup-${provider}-connect" aria-disabled="true">Connect</a>
            <button type="button" id="marketing-setup-${provider}-disconnect">Disconnect</button>
            <button type="button" id="marketing-setup-${provider}-load-accounts">Load accounts</button>
            <div id="marketing-setup-${provider}-account-picker">
              <select id="marketing-setup-${provider}-account-select"><option value="previous-account">Previous account</option></select>
              <button type="button" id="marketing-setup-${provider}-select-account">Select account</button>
            </div>
            <div id="marketing-setup-${provider}-measurement">
              <input data-external-mapping-provider="${provider}" data-canonical-event="LeadSubmitted">
              <button type="button" id="marketing-setup-${provider}-save-measurement">Save mapping</button>
            </div>
          </section>`).join('')}
      </form>
    </div>`, { url, runScripts: 'outside-only' });
  const { window } = dom;
  await new Promise(resolve => window.document.addEventListener('DOMContentLoaded', resolve, { once: true }));
  const requests = [], deadlines = [], errors = [];
  window.console.error = (...args) => errors.push(args.map(String).join(' '));
  window.console.warn = () => {};
  window.setInterval = () => 1;
  const originalTimeout = window.setTimeout.bind(window);
  const originalClear = window.clearTimeout.bind(window);
  window.setTimeout = (callback, delay, ...args) => {
    if (delay !== 45000) return originalTimeout(callback, delay, ...args);
    const entry = { callback, canceled: false };
    deadlines.push(entry);
    return -deadlines.length;
  };
  window.clearTimeout = id => {
    if (id < 0 && deadlines[-id - 1]) deadlines[-id - 1].canceled = true;
    else originalClear(id);
  };
  const instances = new WeakMap();
  window.bootstrap = { Modal: {
    getInstance: element => instances.get(element),
    getOrCreateInstance: element => {
      if (!instances.has(element)) instances.set(element, {
        show() {
          if (element.classList.contains('show')) return;
          element.dispatchEvent(new window.Event('show.bs.modal'));
          element.classList.add('show');
        },
        hide() { element.classList.remove('show'); element.dispatchEvent(new window.Event('hidden.bs.modal')); }
      });
      return instances.get(element);
    }
  } };
  const ready = { canConnect: true, status: 'ready_to_connect', message: null, missingRequirements: [] };
  const snapshot = (owner = 'founder-profile', overrides = {}) => ({
    agentProfileId: owner, agentName: `Owner ${owner}`,
    marketing: { revision: '11111111-1111-1111-1111-111111111111' }, booking: {}, status: {},
    google: { connected, ready: connected, revision: 'google-revision',
      accountName: `Google ${owner}`, connectionSetup: googleSetup || ready },
    tiktok: { connected: false, connectionSetup: ready },
    calendar: { connected, revision: 'calendar-revision' },
    openAi: { connected, exists: connected, revision: 'openai-revision' }, ...overrides
  });
  const json = data => new Response(JSON.stringify(data), { headers: { 'content-type': 'application/json' } });
  window.fetch = async (input, options = {}) => {
    const request = { url: String(input), ...options };
    requests.push(request);
    const parsed = new URL(input, window.location.href);
    if (options.method === 'POST' && postHandler) return postHandler(request, { snapshot, json });
    if (parsed.pathname.endsWith('/marketing-setup')) {
      return setupHandler ? setupHandler(request, { snapshot, json })
        : json(snapshot(parsed.searchParams.get('agentProfileId') || 'founder-profile'));
    }
    return json(parsed.pathname.endsWith('/summary')
      ? { isAvailable: true, pageViews: 0, scopeLabel: 'Founder Personal' }
      : { channels: [], economics: {} });
  };
  window.eval(source);
  window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
  const flush = async () => {
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
  };
  await flush();
  const open = () => window.bootstrap.Modal.getOrCreateInstance(window.document.getElementById('marketingSetupModal')).show();
  return { window, requests, deadlines, errors, open, flush, snapshot, json, dispose: () => window.close() };
}

test('external provider setup shows missing configuration and never navigates an empty connection link', async () => {
  const f = await providerFixture({
    url: 'https://portal.example.test/WebsiteAnalytics/Index?agentProfileId=founder-profile&siteKey=legend#providers',
    googleSetup: {
    canConnect: false, status: 'configuration_required',
    message: 'Google Ads needs workspace setup: OAuth client ID.', missingRequirements: ['OAuth client ID']
  } });
  try {
    const link = f.window.document.getElementById('marketing-setup-google-connect');
    const event = new f.window.MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(event);
    assert.equal(event.defaultPrevented, true);
    f.open();
    await f.flush();
    assert.equal(link.hasAttribute('href'), false);
    assert.equal(link.getAttribute('aria-disabled'), 'true');
    assert.match(f.window.document.getElementById('marketing-setup-google-connection-setup').textContent, /OAuth client ID/);
    const tiktok = new URL(f.window.document.getElementById('marketing-setup-tiktok-connect').href);
    assert.equal(tiktok.pathname, '/WebsiteAnalytics/external-ads/connect');
    assert.equal(tiktok.searchParams.get('provider'), 'tiktok');
    assert.equal(tiktok.searchParams.get('agentProfileId'), 'founder-profile');
    assert.equal(tiktok.searchParams.get('returnUrl'),
      '/WebsiteAnalytics/Index?agentProfileId=founder-profile&siteKey=legend#providers');
    assert.deepEqual(f.errors, []);
  } finally { f.dispose(); }
});

test('external provider callback discards raw transport values and reports failure from canonical setup', async () => {
  const f = await providerFixture({
    url: 'https://portal.example.test/WebsiteAnalytics/Index?agentProfileId=founder-profile&siteKey=legend&provider=google&status=error&message=private-marker&state=private-state#providers',
    googleSetup: { canConnect: false, message: 'Google Ads needs workspace setup: OAuth client ID.' }
  });
  try {
    await f.flush();
    const url = new URL(f.window.location.href);
    assert.equal(url.searchParams.get('agentProfileId'), 'founder-profile');
    assert.equal(url.searchParams.get('siteKey'), 'legend');
    assert.equal(url.hash, '#providers');
    for (const key of ['provider', 'status', 'message', 'state']) assert.equal(url.searchParams.has(key), false);
    assert.equal(f.window.document.getElementById('marketingSetupModal').classList.contains('show'), true);
    assert.match(f.window.document.getElementById('marketing-setup-status').textContent, /OAuth client ID/);
    assert.doesNotMatch(f.window.document.body.textContent, /private-marker|private-state/);
  } finally { f.dispose(); }
});

for (const connected of [false, true]) {
  test(`external provider callback success requires persisted connection status ${connected}`, async () => {
    const f = await providerFixture({ connected,
      url: 'https://portal.example.test/WebsiteAnalytics/Index?agentProfileId=founder-profile&provider=google&status=connected'
    });
    try {
      await f.flush();
      const status = f.window.document.getElementById('marketing-setup-status');
      assert.equal(status.classList.contains('is-success'), connected);
      assert.match(status.textContent, connected ? /verified and saved/ : /could not be connected/);
    } finally { f.dispose(); }
  });
}

test('marketing setup deadline keeps current entries and supports one manual retry', async () => {
  let attempts = 0;
  const f = await providerFixture({ setupHandler: (request, { snapshot, json }) => {
    if (++attempts > 1) return json(snapshot());
    return new Promise((resolve, reject) => {
      request.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
    });
  } });
  try {
    f.open();
    const input = f.window.document.getElementById('marketing-setup-pixel');
    input.value = '123456';
    const deadline = f.deadlines.find(entry => !entry.canceled);
    assert.ok(deadline);
    deadline.callback();
    await f.flush();
    assert.equal(input.value, '123456');
    assert.equal(f.window.document.getElementById('marketing-setup-retry').hidden, false);
    assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, true);
    assert.equal(f.window.document.getElementById('marketing-setup-google-connect').getAttribute('aria-disabled'), 'true');
    f.window.document.getElementById('marketing-setup-retry').click();
    await f.flush();
    assert.equal(attempts, 2);
    assert.equal(f.window.document.getElementById('marketing-setup-retry').hidden, true);
    assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, false);
    assert.match(f.window.document.getElementById('marketing-setup-google-connect').href, /external-ads\/connect/);
    const modal = f.window.bootstrap.Modal.getOrCreateInstance(f.window.document.getElementById('marketingSetupModal'));
    modal.hide(); modal.show();
    await f.flush();
    assert.equal(attempts, 2);
  } finally { f.dispose(); }
});

test('external provider completion cannot overwrite a subsequently selected owner', async () => {
  let finish;
  const f = await providerFixture({ connected: true,
    postHandler: (request, helpers) => new Promise(resolve => { finish = () => resolve(helpers.json(helpers.snapshot('founder-profile'))); })
  });
  try {
    f.open(); await f.flush();
    f.window.document.getElementById('marketing-setup-google-disconnect').click();
    assert.ok(finish);
    const posted = f.requests.find(request => request.method === 'POST');
    assert.equal(JSON.parse(posted.body).agentProfileId, 'founder-profile');
    const select = f.window.document.getElementById('wa-scope-select');
    select.value = 'agent-profile';
    select.dispatchEvent(new f.window.Event('change'));
    await f.flush();
    assert.equal(f.window.document.getElementById('marketing-setup-google-account').textContent, 'Google agent-profile');
    finish(); await f.flush();
    assert.equal(f.window.document.getElementById('marketing-setup-google-account').textContent, 'Google agent-profile');
    assert.match(f.window.document.getElementById('marketing-setup-scope-label').textContent, /agent-profile/);
  } finally { f.dispose(); }
});

for (const outcome of ['delayed', 'failed']) {
  test(`marketing setup rejects every old-owner action while the selected owner load is ${outcome}`, async () => {
    let completeRead;
    const f = await providerFixture({ connected: true, setupHandler: (request, { snapshot, json }) => {
      const owner = new URL(request.url, 'https://portal.example.test').searchParams.get('agentProfileId');
      if (owner === 'founder-profile') return json(snapshot(owner));
      if (outcome === 'failed') return new Response('', { status: 503 });
      return new Promise(resolve => { completeRead = () => resolve(json(snapshot(owner))); });
    } });
    try {
      f.open(); await f.flush();
      f.window.document.getElementById('marketing-setup-openai-api-key').value = 'old-owner-key';
      const scope = f.window.document.getElementById('wa-scope-select');
      scope.value = 'agent-profile';
      scope.dispatchEvent(new f.window.Event('change'));
      await f.flush();
      const actions = [
        'google-disconnect', 'google-load-accounts', 'google-select-account', 'google-save-measurement',
        'tiktok-disconnect', 'tiktok-load-accounts', 'tiktok-select-account', 'tiktok-save-measurement',
        'calendar-disconnect', 'openai-verify', 'openai-connect-submit', 'openai-disconnect'
      ];
      for (const action of actions) {
        const button = f.window.document.getElementById(`marketing-setup-${action}`);
        assert.equal(button.disabled, true, action);
        // Direct dispatch exercises the canonical handler guard even if a
        // stale client dispatch bypasses the browser's disabled-button behavior.
        button.dispatchEvent(new f.window.MouseEvent('click', { bubbles: true, cancelable: true }));
      }
      f.window.document.getElementById('marketing-setup-form').dispatchEvent(
        new f.window.Event('submit', { bubbles: true, cancelable: true }));
      for (const provider of ['meta', 'calendar', 'google', 'tiktok']) {
        const link = f.window.document.getElementById(`marketing-setup-${provider}-connect`);
        const event = new f.window.MouseEvent('click', { bubbles: true, cancelable: true });
        link.dispatchEvent(event);
        assert.equal(event.defaultPrevented, true, provider);
        assert.equal(link.hasAttribute('href'), false, provider);
      }
      await f.flush();
      assert.equal(f.requests.filter(request => request.method === 'POST').length, 0);
      assert.equal(f.requests.filter(request => request.url.includes('/external-ads/accounts')).length, 0);
      assert.equal(f.window.document.getElementById('marketing-setup-openai-api-key').value, '');
      assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, true);
      assert.equal(f.window.document.getElementById('marketing-setup-google-account').closest('section').hidden, true);
      if (completeRead) {
        completeRead(); await f.flush();
        assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, false);
        assert.equal(f.window.document.getElementById('marketing-setup-google-account').textContent, 'Google agent-profile');
      } else assert.equal(f.window.document.getElementById('marketing-setup-retry').hidden, false);
    } finally { f.dispose(); }
  });
}

test('marketing setup serializes manual reads and writes without aborting or repeating a mutation', async () => {
  let readCount = 0, finishRead, finishPost;
  const f = await providerFixture({ connected: true,
    setupHandler: (request, { snapshot, json }) => {
      if (++readCount === 1) return json(snapshot());
      return new Promise(resolve => { finishRead = () => resolve(json(snapshot())); });
    },
    postHandler: (request, { snapshot, json }) => new Promise(resolve => {
      finishPost = () => resolve(json(snapshot('founder-profile', {
        google: { connected: false, connectionSetup: { canConnect: true } }
      })));
    })
  });
  try {
    f.open(); await f.flush();
    const retry = f.window.document.getElementById('marketing-setup-retry');
    retry.dispatchEvent(new f.window.MouseEvent('click'));
    assert.ok(finishRead);
    const disconnect = f.window.document.getElementById('marketing-setup-google-disconnect');
    disconnect.dispatchEvent(new f.window.MouseEvent('click'));
    f.window.document.getElementById('marketing-setup-form').dispatchEvent(
      new f.window.Event('submit', { cancelable: true }));
    assert.equal(f.requests.filter(request => request.method === 'POST').length, 0);
    finishRead(); await f.flush();
    disconnect.click();
    assert.ok(finishPost);
    disconnect.dispatchEvent(new f.window.MouseEvent('click'));
    retry.dispatchEvent(new f.window.MouseEvent('click'));
    await f.flush();
    const posts = f.requests.filter(request => request.method === 'POST');
    assert.equal(posts.length, 1);
    assert.equal(posts[0].signal.aborted, false);
    assert.equal(readCount, 2);
    finishPost(); await f.flush();
    assert.equal(disconnect.hidden, true);
    assert.match(f.window.document.getElementById('marketing-setup-google-status').textContent, /not connected/);
  } finally { f.dispose(); }
});

test('marketing setup same-owner refresh failure preserves admitted entries and actions', async () => {
  let reads = 0;
  const f = await providerFixture({ connected: true, setupHandler: (request, { snapshot, json }) =>
    ++reads === 1 ? json(snapshot()) : new Response('', { status: 503 })
  });
  try {
    f.open(); await f.flush();
    const input = f.window.document.getElementById('marketing-setup-pixel');
    input.value = '123456';
    f.window.document.getElementById('marketing-setup-retry').dispatchEvent(new f.window.MouseEvent('click'));
    await f.flush();
    assert.equal(input.value, '123456');
    assert.equal(input.disabled, false);
    assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, false);
    assert.equal(f.window.document.getElementById('marketing-setup-google-disconnect').disabled, false);
    assert.match(f.window.document.getElementById('marketing-setup-google-connect').href, /external-ads\/connect/);
    assert.equal(f.window.document.getElementById('marketing-setup-retry').hidden, false);
  } finally { f.dispose(); }
});

for (const action of ['save', 'calendar-disconnect', 'openai-verify', 'openai-connect-submit', 'openai-disconnect']) {
  test(`marketing setup ${action} completion cannot alter a subsequently selected owner`, async () => {
    let finish;
    const f = await providerFixture({ connected: true,
      postHandler: (request, { snapshot, json }) => new Promise(resolve => {
        finish = () => resolve(json(snapshot('founder-profile')));
      })
    });
    try {
      f.open(); await f.flush();
      f.window.document.getElementById('marketing-setup-openai-api-key').value = 'old-owner-key';
      if (action === 'save') {
        f.window.document.getElementById('marketing-setup-form').dispatchEvent(
          new f.window.Event('submit', { cancelable: true }));
      } else f.window.document.getElementById(`marketing-setup-${action}`).click();
      assert.ok(finish);
      const posted = f.requests.find(request => request.method === 'POST');
      assert.equal(JSON.parse(posted.body).agentProfileId, 'founder-profile');
      const scope = f.window.document.getElementById('wa-scope-select');
      scope.value = 'agent-profile';
      scope.dispatchEvent(new f.window.Event('change'));
      await f.flush();
      const currentKey = f.window.document.getElementById('marketing-setup-openai-api-key');
      currentKey.value = 'new-owner-entry';
      const status = f.window.document.getElementById('marketing-setup-status').textContent;
      finish(); await f.flush();
      assert.equal(f.window.document.getElementById('marketing-setup-google-account').textContent, 'Google agent-profile');
      assert.equal(f.window.document.getElementById('marketing-setup-status').textContent, status);
      assert.equal(currentKey.value, 'new-owner-entry');
      assert.equal(posted.signal.aborted, false);
    } finally { f.dispose(); }
  });
}

test('marketing setup owner changes do not cancel another owner’s already-sent mutation', async () => {
  const pending = [];
  const f = await providerFixture({ connected: true,
    postHandler: (request, { snapshot, json }) => new Promise(resolve => {
      pending.push({ request, finish: () => resolve(json(snapshot(JSON.parse(request.body).agentProfileId))) });
    })
  });
  try {
    f.open(); await f.flush();
    f.window.document.getElementById('marketing-setup-google-disconnect').click();
    const scope = f.window.document.getElementById('wa-scope-select');
    scope.value = 'agent-profile';
    scope.dispatchEvent(new f.window.Event('change'));
    await f.flush();
    f.window.document.getElementById('marketing-setup-google-disconnect').click();
    assert.equal(pending.length, 2);
    assert.equal(pending[0].request.signal.aborted, false);
    assert.equal(pending[1].request.signal.aborted, false);
    pending[1].finish(); await f.flush();
    pending[0].finish(); await f.flush();
    assert.equal(f.window.document.getElementById('marketing-setup-google-account').textContent, 'Google agent-profile');
  } finally { f.dispose(); }
});

test('marketing setup revisiting an owner with a pending write requires reconciliation before another mutation', async () => {
  const pending = [];
  const f = await providerFixture({ connected: true,
    postHandler: (request, { snapshot, json }) => new Promise(resolve => {
      pending.push({ request, finish: () => resolve(json(snapshot(JSON.parse(request.body).agentProfileId))) });
    })
  });
  try {
    f.open(); await f.flush();
    const disconnect = f.window.document.getElementById('marketing-setup-google-disconnect');
    disconnect.click();
    const scope = f.window.document.getElementById('wa-scope-select');
    for (const owner of ['agent-profile', 'founder-profile']) {
      scope.value = owner;
      scope.dispatchEvent(new f.window.Event('change'));
      await f.flush();
    }
    disconnect.dispatchEvent(new f.window.MouseEvent('click'));
    assert.equal(pending.length, 1);
    assert.equal(disconnect.disabled, true);
    assert.equal(pending[0].request.signal.aborted, false);
    pending[0].finish(); await f.flush();
    assert.equal(f.window.document.getElementById('marketing-setup-retry').hidden, false);
    assert.equal(disconnect.disabled, true);
    disconnect.dispatchEvent(new f.window.MouseEvent('click'));
    assert.equal(pending.length, 1);
    f.window.document.getElementById('marketing-setup-retry').click();
    await f.flush();
    disconnect.click();
    assert.equal(pending.length, 2);
    pending[1].finish(); await f.flush();
  } finally { f.dispose(); }
});

for (const outcome of ['network-error', 'empty-result']) {
  test(`marketing setup reconciles an uncertain submitted ${outcome} before another action`, async () => {
    let posts = 0;
    const f = await providerFixture({ connected: true,
      postHandler: (request, { snapshot, json }) => {
        posts += 1;
        if (posts > 1) return json(snapshot());
        if (outcome === 'network-error') throw new TypeError('Fixture network failure');
        return new Response('', { headers: { 'content-type': 'application/json' } });
      }
    });
    try {
      f.open(); await f.flush();
      const disconnect = f.window.document.getElementById('marketing-setup-google-disconnect');
      disconnect.click(); await f.flush();
      assert.equal(posts, 1);
      assert.equal(disconnect.disabled, true);
      assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, true);
      disconnect.dispatchEvent(new f.window.MouseEvent('click'));
      assert.equal(posts, 1);
      const retry = f.window.document.getElementById('marketing-setup-retry');
      assert.equal(retry.hidden, false);
      retry.click(); await f.flush();
      assert.equal(posts, 1);
      assert.equal(disconnect.disabled, false);
      disconnect.click(); await f.flush();
      assert.equal(posts, 2);
    } finally { f.dispose(); }
  });
}

test('marketing setup local validation preserves admitted edits without submitting a write', async () => {
  const f = await providerFixture({ connected: true });
  try {
    f.open(); await f.flush();
    const pixel = f.window.document.getElementById('marketing-setup-pixel');
    pixel.value = 'invalid-pixel';
    f.window.document.getElementById('marketing-setup-form').dispatchEvent(
      new f.window.Event('submit', { cancelable: true }));
    await f.flush();
    assert.equal(f.requests.filter(request => request.method === 'POST').length, 0);
    assert.equal(pixel.value, 'invalid-pixel');
    assert.equal(pixel.disabled, false);
    assert.equal(f.window.document.getElementById('marketing-setup-save').disabled, false);
    assert.match(f.window.document.getElementById('marketing-setup-status').textContent, /only digits/);
  } finally { f.dispose(); }
});

for (const initialSite of ['legend', 'protect']) {
  test(`Founder website links follow initial ${initialSite} and both site switches while product routes stay Protect`, async () => {
    const f = await fixture({ founder: true, siteKey: initialSite });
    try {
      for (const siteKey of [initialSite, initialSite === 'legend' ? 'protect' : 'legend', initialSite]) {
        f.window.document.querySelector(`[data-site-key="${siteKey}"]`).click();
        const expected = `https://${siteKey}.example.test/`;
        assert.equal(f.window.document.getElementById('growth-base-link').textContent, expected);
        assert.equal(f.window.document.getElementById('growth-open-base').href, expected);
        f.window.document.getElementById('growth-copy-base').click();
        assert.equal(f.copied.at(-1), expected);
        assert.equal(f.window.document.getElementById('landing-routes-base-url').textContent, 'https://protect.example.test/');
        const product = f.window.document.querySelector('.product-link-copy');
        assert.equal(product.dataset.linkUrl, 'https://protect.example.test/quote/life');
      }
    } finally { await f.dispose(); }
  });
}

for (const selectedAgent of [false, true]) {
  test(`agent personal and product links retain their prefix with Founder viewing agent ${selectedAgent}`, async () => {
    const f = await fixture({ founder: selectedAgent, selectedAgent });
    try {
      for (const siteKey of ['legend', 'protect', 'legend']) {
        f.window.document.querySelector(`[data-site-key="${siteKey}"]`).click();
        assert.equal(f.window.document.getElementById('growth-open-base').href, 'https://protect.example.test/a/agent-one');
        assert.equal(f.window.document.querySelector('.product-link-copy').dataset.linkUrl, 'https://protect.example.test/a/agent-one/quote/life');
      }
    } finally { await f.dispose(); }
  });
}

for (const initialSite of ['legend', 'protect']) {
  test(`channel outcomes requests retain Founder site ${initialSite} through both switches`, async () => {
    const f = await fixture({ founder: true, siteKey: initialSite });
    try {
      for (const siteKey of [initialSite, initialSite === 'legend' ? 'protect' : 'legend', initialSite]) {
        f.window.document.querySelector(`[data-site-key="${siteKey}"]`).click();
        await new Promise(resolve => setTimeout(resolve, 10));
        const request = new URL(f.calls.filter(url => url.includes('/marketing-manager/performance')).at(-1), f.window.location.href);
        assert.equal(request.searchParams.get('siteKey'), siteKey);
        assert.equal(request.searchParams.get('agentProfileId'), 'founder-profile');
        assert.equal(request.searchParams.get('qualityMode'), '0');
      }
    } finally { f.dom.window.close(); }
  });
}

test('channel outcomes retain selected Founder site when the request omits the profile id', async () => {
  const f = await fixture({ founder: true, global: true });
  try {
    for (const siteKey of ['legend', 'protect', 'legend']) {
      f.window.document.querySelector(`[data-site-key="${siteKey}"]`).click();
      await new Promise(resolve => setTimeout(resolve, 10));
      const request = new URL(f.calls.filter(url => url.includes('/marketing-manager/performance')).at(-1), f.window.location.href);
      assert.equal(request.searchParams.get('siteKey'), siteKey);
      assert.equal(request.searchParams.has('agentProfileId'), false);
    }
  } finally { f.dom.window.close(); }
});

for (const scope of [{}, { founder: true, selectedAgent: true }, { business: true }]) {
  test(`channel outcomes do not transmit Founder site into other scope ${JSON.stringify(scope)}`, async () => {
    const f = await fixture(scope);
    try {
      const request = new URL(f.calls.filter(url => url.includes('/marketing-manager/performance')).at(-1), f.window.location.href);
      assert.equal(request.searchParams.has('siteKey'), false);
      assert.equal(request.searchParams.get('agentProfileId'), scope.business ? null : scope.selectedAgent ? 'agent-profile' : 'founder-profile');
    } finally { f.dom.window.close(); }
  });
}

for (const global of [false, true]) {
  test(`growth plan POST follows both Founder sites with omitted profile ${global}`, async () => {
    const f = await fixture({ founder: true, global });
    try {
      for (const siteKey of ['legend', 'protect', 'legend']) {
        f.window.document.querySelector(`[data-site-key="${siteKey}"]`).click();
        f.window.document.getElementById('marketing-manager-build').click();
        await new Promise(resolve => setTimeout(resolve, 10));
        const request = f.requests.filter(r => r.method === 'POST').at(-1);
        const url = new URL(request.url, f.window.location.href);
        assert.equal(url.pathname, '/WebsiteAnalytics/marketing-manager/plan');
        assert.equal(url.searchParams.get('siteKey'), siteKey);
        assert.equal(request.headers.RequestVerificationToken, 'test-request-token');
        const body = JSON.parse(request.body);
        assert.equal(body.agentProfileId, global ? undefined : 'founder-profile');
        assert.equal(body.siteKey, undefined);
        assert.equal(body.goal.goal, 'Find the next qualified lead');
      }
    } finally { f.dom.window.close(); }
  });
}

for (const scope of [{}, { founder: true, selectedAgent: true }, { business: true }]) {
  test(`growth plan POST keeps its existing owner and omits Founder site in scope ${JSON.stringify(scope)}`, async () => {
    const f = await fixture(scope);
    try {
      f.window.document.getElementById('marketing-manager-build').click();
      await new Promise(resolve => setTimeout(resolve, 10));
      const request = f.requests.filter(r => r.method === 'POST').at(-1);
      const url = new URL(request.url, f.window.location.href);
      assert.equal(url.searchParams.has('siteKey'), false);
      assert.equal(url.pathname, scope.business ? '/business/11111111-1111-1111-1111-111111111111/analytics/marketing-manager/plan' : '/WebsiteAnalytics/marketing-manager/plan');
      assert.equal(JSON.parse(request.body).agentProfileId, scope.business ? undefined : scope.selectedAgent ? 'agent-profile' : 'founder-profile');
      assert.equal(request.headers.RequestVerificationToken, 'test-request-token');
    } finally { f.dom.window.close(); }
  });
}

for (const device of [true, false]) {
  test(`summary refresh loads canonical marketing projections with device module ${device ? 'present' : 'absent'}`, async () => {
    const f = await fixture({ device });
    try {
      assert.equal(f.window.document.getElementById('kpi-pageviews').textContent, '7');
      assert.equal(f.calls.filter(url => url.includes('/marketing-manager/performance')).length, 1);
      assert.equal(f.calls.filter(url => url.includes('/growth-economics')).length, 0);
      assert.match(f.window.document.getElementById('channel-performance-grid').textContent, /No channel outcomes/);
      assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /12/);
      assert.deepEqual(f.errors, []);
      for (const url of f.calls) {
        assert.equal(new URL(url, 'https://portal.example.test').searchParams.get('agentProfileId'), 'founder-profile');
      }
    } finally { f.dom.window.close(); }
  });
}

test('concurrent device refreshes share decoded data without consuming a response twice', async () => {
  const f = await fixture();
  try {
    const results = await Promise.all([
      f.window.websiteAnalyticsDeviceIntelligence.loadCurrentView(),
      f.window.websiteAnalyticsDeviceIntelligence.loadCurrentView()
    ]);
    assert.deepEqual(results, [true, true]);
    assert.equal(f.calls.filter(url => url.includes('/DeviceIntelligence')).length, 1);
    assert.equal(f.window.document.getElementById('deviceSessions').textContent, '2');
  } finally { f.dom.window.close(); }
});

test('loading the script again does not duplicate initialization or summary requests', async () => {
  const f = await fixture();
  try {
    const before = f.calls.length;
    f.window.eval(source);
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(f.calls.length, before);
    assert.deepEqual(f.errors, []);
  } finally { f.dom.window.close(); }
});


const settle = () => new Promise(resolve => setTimeout(resolve, 10));
const reportResponse = spend => new Response(JSON.stringify({ channels: [], economics: { totalMarketingSpend: spend, channels: [] } }), { headers: { 'content-type': 'application/json' } });

test('manual refresh preserves the same snapshot and scroll; scope changes reject the older completion', async () => {
  const f = await fixture({ founder: true });
  try {
    const pending = [];
    const originalFetch = f.window.fetch;
    f.window.fetch = (url, options) => String(url).includes('/marketing-manager/performance')
      ? new Promise(resolve => pending.push({ resolve, signal: options.signal })) : originalFetch(url, options);
    const grid = f.window.document.getElementById('growth-economics-grid');
    const marker = f.window.document.createElement('span');
    grid.append(marker);
    grid.scrollTop = 47;
    const refresh = f.window.document.getElementById('analytics-refresh');
    refresh.click();
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /12/);
    assert.equal(grid.firstChild, marker);
    assert.equal(grid.scrollTop, 47);
    assert.equal(refresh.disabled, true);
    f.window.document.querySelector('[data-site-key="protect"]').click();
    await settle();
    assert.equal(pending[0].signal.aborted, true);
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
    pending[0].resolve(reportResponse(99));
    await settle();
    assert.equal(refresh.disabled, true, 'old finally must not release the new request');
    pending[1].resolve(reportResponse(24));
    await settle();
    assert.equal(refresh.disabled, false);
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /24/);
    f.window.fetch = async () => new Response('', { status: 503 });
    refresh.click();
    await settle();
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
  } finally { f.window.close(); }
});


for (const transition of ['range', 'site', 'owner']) {
  test(`unavailable ${transition} changes clear prior hints, warnings, and health labels while same-scope refresh retains them`, async () => {
    const summaryPending = [], healthPending = [];
    let summaryCalls = 0, healthCalls = 0;
    const response = data => new Response(JSON.stringify(data), { headers: { 'content-type': 'application/json' } });
    const previous = { isAvailable: true, rangeLabel: 'Today', scopeLabel: 'Founder Personal',
      pageViews: 7, sessions: 2, topSource: 'prior-source', topPage: 'prior-page', topCta: 'prior-cta',
      intentLowSample: true, sessionLowSample: true };
    const f = await fixture({ founder: true,
      summaryHandler: () => ++summaryCalls === 1 ? response(previous)
        : new Promise((resolve, reject) => summaryPending.push({ resolve, reject })),
      healthHandler: () => ++healthCalls === 1 ? response({ rangeLabel: 'Today', metaHealthStatus: 'Healthy' })
        : new Promise((resolve, reject) => healthPending.push({ resolve, reject }))
    });
    try {
      const d = f.window.document, text = id => d.getElementById(id).textContent;
      assert.match(text('mod-traffic-meta'), /prior-source/);
      assert.match(text('kpi-intent-warning'), /Low sample/);
      assert.equal(text('mh-range-label'), 'Today');
      d.getElementById('analytics-refresh').click();
      assert.match(text('mod-page-meta'), /prior-page/, 'same-scope pending refresh retains current evidence');
      assert.match(text('kpi-session-warning'), /Low sample/);
      if (transition === 'range') d.querySelector('[data-range="7d"]').click();
      if (transition === 'site') d.querySelector('[data-site-key="protect"]').click();
      if (transition === 'owner') {
        const owner = d.getElementById('wa-scope-select');
        owner.value = 'agent-profile';
        owner.dispatchEvent(new f.window.Event('change'));
      }
      await settle();
      const assertUnavailable = () => {
        for (const id of ['mod-traffic-meta', 'mod-page-meta', 'mod-cta-meta'])
          assert.doesNotMatch(text(id), /prior-/);
        assert.equal(text('kpi-intent-warning'), '');
        assert.equal(text('kpi-session-warning'), '');
        assert.equal(text('mh-verdict'), 'Unavailable');
        assert.equal(d.getElementById('mh-verdict').className, 'wa-health-verdict unavailable');
        if (transition === 'range') {
          assert.notEqual(text('mh-range-label'), 'Today');
          assert.doesNotMatch(text('ai-drawer-scope-label'), /Today/);
        }
      };
      assertUnavailable();
      assert.equal(summaryPending.length, 2);
      assert.equal(healthPending.length, 2);
      summaryPending[0].resolve(response(previous));
      healthPending[0].resolve(response({ rangeLabel: 'Today', metaHealthStatus: 'Healthy' }));
      await settle();
      assertUnavailable();
      summaryPending[1].reject(new Error('Selected summary unavailable'));
      healthPending[1].reject(new Error('Selected health unavailable'));
      await settle();
      assertUnavailable();
      assert.equal(text('kpi-pageviews'), '—');
      assert.equal(d.getElementById('analytics-refresh').disabled, false);
    } finally { f.window.close(); }
  });
}

test('a selected-range failure clears a previous no-traffic claim instead of treating unknown data as zero', async () => {
  let calls = 0;
  const f = await fixture({ founder: true, summaryHandler: async () => ++calls === 1
    ? new Response(JSON.stringify({ isAvailable: true, rangeLabel: 'Today', pageViews: 0, sessions: 0 }))
    : new Response('', { status: 503 }) });
  try {
    const d = f.window.document, empty = d.getElementById('traffic-quality-empty-state');
    assert.equal(empty.hidden, false);
    assert.match(empty.textContent, /No real human traffic/);
    d.querySelector('[data-range="7d"]').click();
    assert.equal(empty.hidden, true);
    assert.equal(empty.textContent, '');
    await settle();
    assert.equal(empty.hidden, true);
    assert.equal(empty.textContent, '');
    assert.equal(d.getElementById('kpi-pageviews').textContent, '—');
  } finally { f.window.close(); }
});

test('first mount loads once, focus and visibility do not reload, and repeated manual actions share one pending read', async () => {
  const pending = [];
  const f = await fixture({ founder: true, performanceHandler: (url, options) => new Promise(resolve => pending.push({ resolve, signal: options.signal })) });
  try {
    assert.equal(pending.length, 1);
    assert.deepEqual(f.polls, []);
    const before = f.calls.length;
    f.window.dispatchEvent(new f.window.Event('focus'));
    f.window.document.dispatchEvent(new f.window.Event('visibilitychange'));
    for (let i = 0; i < 3; i++) f.window.document.getElementById('analytics-refresh').dispatchEvent(new f.window.Event('click'));
    await settle();
    assert.equal(f.calls.length, before);
    assert.equal(pending.length, 1);
    assert.equal(pending[0].signal.aborted, false);
    pending[0].resolve(reportResponse(42));
    await settle();
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /42/);
    const count = f.calls.length;
    for (const id of ['growthEconomicsModal', 'channelOutcomesModal'])
      f.window.document.getElementById(id).dispatchEvent(new f.window.Event('show.bs.modal'));
    assert.equal(f.calls.length, count, 'opening either captured report must not refetch');
  } finally { f.window.close(); }
});

test('a request deadline remains unavailable and releases manual refresh for retry', async () => {
  const pending = [];
  const f = await fixture({ founder: true, captureDeadlines: true, performanceHandler: (url, options) => new Promise((resolve, reject) => {
    pending.push({ resolve, signal: options.signal });
    options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
  }) });
  try {
    f.deadlines.forEach(expire => expire());
    await settle();
    assert.match(f.window.document.getElementById('channel-performance-grid').textContent, /took too long/);
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
    const refresh = f.window.document.getElementById('analytics-refresh');
    assert.equal(refresh.disabled, false);
    refresh.click();
    assert.equal(pending.length, 2);
    assert.equal(pending[1].signal.aborted, false);
    pending[1].resolve(reportResponse(31));
    await settle();
  } finally { f.window.close(); }
});

for (const stalled of ['summary', 'health']) {
  test(`a stalled ${stalled} has a bounded deadline and cannot strand manual refresh or erase independent economics`, async () => {
    const handler = (url, options) => new Promise((resolve, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true }));
    const f = await fixture({ captureDeadlines: true, [stalled === 'summary' ? 'summaryHandler' : 'healthHandler']: handler });
    try {
      assert.equal(f.window.document.getElementById('analytics-refresh').disabled, true);
      assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /12/);
      f.deadlines.forEach(expire => expire());
      await settle();
      assert.equal(f.window.document.getElementById('analytics-refresh').disabled, false);
      assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /12/);
      assert.match(f.window.document.getElementById('analytics-refresh-status').textContent, /too long|unavailable/);
    } finally { f.window.close(); }
  });
}

for (const failure of [false, true]) {
  test(`late prior-range summary ${failure ? 'rejection' : 'success'} cannot overwrite the selected report or clear its busy state`, async () => {
    const pending = [];
    const f = await fixture({ founder: true, summaryHandler: (url, options) => new Promise((resolve, reject) => pending.push({ resolve, reject, signal: options.signal })) });
    try {
      f.window.document.querySelector('[data-range="7d"]').click();
      await settle();
      assert.equal(pending.length, 2);
      if (failure) pending[0].reject(new Error('prior owner error'));
      else pending[0].resolve(new Response(JSON.stringify({ isAvailable: true, pageViews: 99, sessions: 1 })));
      await settle();
      assert.equal(f.window.document.getElementById('analytics-refresh').disabled, true);
      assert.notEqual(f.window.document.getElementById('kpi-pageviews').textContent, '99');
      pending[1].resolve(new Response(JSON.stringify({ isAvailable: true, pageViews: 24, sessions: 1 })));
      await settle();
      assert.equal(f.window.document.getElementById('kpi-pageviews').textContent, '24');
      assert.equal(f.window.document.getElementById('analytics-refresh').disabled, false);
      assert.equal(f.errors.some(error => error.includes('prior owner error')), false);
    } finally { f.window.close(); }
  });
}

for (const status of [200, 503]) {
  test(`HTML error content with HTTP ${status} is never logged or rendered`, async () => {
    const secret = 'do-not-log-hidden-form-token';
    const f = await fixture({ summaryHandler: async () => new Response(`<form><input value="${secret}"></form>`, { status, headers: { 'content-type': 'text/html' } }) });
    try {
      assert.equal(f.window.document.body.textContent.includes(secret), false);
      assert.equal(f.errors.some(error => error.includes(secret) || error.includes('<form>')), false);
      assert.match(f.window.document.getElementById('analytics-refresh-status').textContent, /HTTP 503|invalid analytics response/);
      assert.equal(f.window.document.getElementById('analytics-refresh').disabled, false);
    } finally { f.window.close(); }
  });
}

test('the main report controls own one complete dialog each and page scripts use the host Bootstrap owner', () => {
  const view = readFileSync(new URL('../../AgentPortal/Views/WebsiteAnalytics/Index.cshtml', import.meta.url), 'utf8');
  for (const id of ['growthEconomicsModal', 'channelOutcomesModal', 'growth-economics-spend', 'growth-economics-grid', 'channel-performance-grid'])
    assert.equal(view.split(`id="${id}"`).length - 1, 1);
  assert.match(view, /@section Scripts/);
  assert.doesNotMatch(view, /bootstrap\.bundle\.min\.js/);
  assert.match(view, /id="growth-economics-open"[^>]*data-bs-target="#growthEconomicsModal"/);
  assert.match(view, /id="channel-performance-open"[^>]*data-bs-target="#channelOutcomesModal"/);
});

for (const failure of [false, true]) {
  test(`composite behavior ${failure ? 'failure' : 'completion'} from a previous view cannot repaint the current dialog`, async () => {
    const f = await fixture({ founder: true });
    try {
      const pending = [];
      const originalFetch = f.window.fetch;
      f.window.fetch = (url, options) => String(url).includes('/behavior/')
        ? new Promise((resolve, reject) => pending.push({ url, resolve, reject, signal: options.signal })) : originalFetch(url, options);
      const modal = f.window.document.getElementById('behaviorModal');
      modal.dispatchEvent(new f.window.Event('show.bs.modal'));
      assert.equal(pending.length, 5);
      f.window.document.querySelector('[data-site-key="protect"]').click();
      await settle();
      assert.equal(pending.length, 10);
      pending.slice(5).forEach(request => request.resolve(new Response(JSON.stringify({ totalSessions: 24 }))));
      await settle();
      assert.equal(f.window.document.getElementById('bhvr-total-sessions').textContent, '24');
      assert.equal(modal.querySelector('.modal-body').inert, false);
      pending.slice(0, 5).forEach(request => failure ? request.reject(new Error('old behavior failure')) : request.resolve(new Response(JSON.stringify({ totalSessions: 99 }))));
      await settle();
      assert.equal(f.window.document.getElementById('bhvr-total-sessions').textContent, '24');
      assert.equal(modal.querySelector('.modal-body').inert, false);
      assert.equal(f.errors.some(error => error.includes('old behavior failure')), false);
      modal.dispatchEvent(new f.window.Event('hidden.bs.modal'));
      f.window.document.querySelector('[data-range="7d"]').click();
      assert.equal(modal.querySelector('.modal-body').inert, true);
      assert.match(modal.querySelector('[data-analytics-detail-status]').textContent, /Previous view/);
    } finally { await f.dispose(); }
  });
}

test('overlapping same-scope behavior reads preserve the newer response ownership', async () => {
  const f = await fixture();
  try {
    const pending = [];
    const originalFetch = f.window.fetch;
    f.window.fetch = (url, options) => String(url).includes('/behavior/')
      ? new Promise(resolve => pending.push({ resolve, signal: options.signal })) : originalFetch(url, options);
    const modal = f.window.document.getElementById('behaviorModal');
    modal.dispatchEvent(new f.window.Event('show.bs.modal'));
    modal.dispatchEvent(new f.window.Event('show.bs.modal'));
    assert.equal(pending.length, 10);
    pending.slice(5).forEach(request => request.resolve(new Response(JSON.stringify({ totalSessions: 24 }))));
    await settle();
    pending.slice(0, 5).forEach(request => request.resolve(new Response(JSON.stringify({ totalSessions: 99 }))));
    await settle();
    assert.equal(f.window.document.getElementById('bhvr-total-sessions').textContent, '24');
  } finally { await f.dispose(); }
});

for (const failure of ['conversions', 'summary']) {
  test(`a rejected filtered ${failure} completes Conversion Center as unavailable without a fabricated denominator`, async () => {
    const f = await fixture();
    try {
      const modal = f.window.document.getElementById('convModal');
      modal.dispatchEvent(new f.window.Event('show.bs.modal'));
      await settle();
      const originalFetch = f.window.fetch;
      f.window.fetch = (url, options) => {
        const path = new URL(String(url), f.window.location.href).pathname;
        return path.endsWith('/' + failure) ? Promise.resolve(new Response('', { status: 503 })) : originalFetch(url, options);
      };
      modal.querySelector('[data-type="paid"]').click();
      await settle();
      const body = modal.querySelector('.modal-body');
      assert.equal(body.getAttribute('aria-busy'), 'false');
      assert.equal(body.inert, true);
      assert.match(modal.querySelector('[data-analytics-detail-status]').textContent, /unavailable/);
      assert.equal(f.window.document.getElementById('conv-session').textContent, '—');
      assert.equal(f.window.document.getElementById('conv-intent').textContent, '—');
      f.window.fetch = originalFetch;
      const retry = modal.querySelector('[data-analytics-detail-status] [data-analytics-refresh]');
      assert.equal(retry.disabled, false);
      retry.click();
      await settle();
      assert.equal(body.inert, false);
      assert.equal(body.getAttribute('aria-busy'), 'false');
      assert.equal(f.window.document.getElementById('conv-total').textContent, '2');
    } finally { await f.dispose(); }
  });
}

test('Advertising loading failure clears stale controls and permits an explicit successful retry', async () => {
  const f = await fixture();
  try {
    const originalFetch = f.window.fetch;
    let fail = true;
    f.window.fetch = (url, options) => String(url).includes('/advertising?')
      ? Promise.resolve(fail ? new Response('<form>private-token</form>', { status: 500, headers: { 'content-type': 'text/html' } }) : new Response(JSON.stringify({ connection: { connected: false }, campaigns: [], proposals: [] })))
      : originalFetch(url, options);
    const modal = f.window.document.getElementById('advertisingCommandModal');
    modal.dispatchEvent(new f.window.Event('show.bs.modal'));
    await settle();
    const refresh = f.window.document.getElementById('advertising-command-refresh');
    assert.equal(refresh.disabled, false);
    assert.match(f.window.document.getElementById('advertising-command-status').textContent, /HTTP 500/);
    assert.equal(f.window.document.getElementById('advertising-command-campaign-count').textContent, '—');
    assert.equal(f.window.document.getElementById('advertising-command-promote').disabled, true);
    assert.equal(f.window.document.body.textContent.includes('private-token'), false);
    assert.equal(f.errors.some(error => error.includes('private-token')), false);
    fail = false;
    refresh.click();
    await settle();
    assert.equal(refresh.disabled, false);
    assert.equal(f.window.document.getElementById('advertising-command-campaign-count').textContent, '0');
    assert.match(f.window.document.getElementById('advertising-command-status').textContent, /current/);
  } finally { await f.dispose(); }
});

test('fast conversions reuse the delayed current summary after a scope switch', async () => {
  const pending = [];
  let count = 0;
  const summary = value => new Response(JSON.stringify({ isAvailable: true, pageViews: 24, sessions: 3, sessionConversionRate: value }));
  const f = await fixture({ founder: true, summaryHandler: () => ++count === 1 ? Promise.resolve(summary(1.5)) : new Promise(resolve => pending.push(resolve)) });
  try {
    const modal = f.window.document.getElementById('convModal');
    modal.dispatchEvent(new f.window.Event('show.bs.modal'));
    await settle();
    assert.equal(f.window.document.getElementById('conv-session').textContent, '1.5%');
    f.window.document.querySelector('[data-site-key="protect"]').click();
    await settle();
    assert.equal(pending.length, 1, 'the denominator must reuse the page summary request');
    assert.equal(count, 2);
    assert.equal(modal.querySelector('.modal-body').getAttribute('aria-busy'), 'true');
    pending[0](summary(2.5));
    await settle();
    assert.equal(f.window.document.getElementById('conv-total').textContent, '2');
    assert.equal(f.window.document.getElementById('conv-session').textContent, '2.5%');
    assert.equal(modal.querySelector('.modal-body').inert, false);
    assert.equal(modal.querySelector('.modal-body').getAttribute('aria-busy'), 'false');
    assert.equal(f.window.document.getElementById('analytics-refresh').disabled, false);
  } finally { await f.dispose(); }
});

test('a failed current summary is not retried implicitly when conversions finish later', async () => {
  let summaryCalls = 0;
  const f = await fixture({ founder: true, summaryHandler: () => {
    summaryCalls += 1;
    return Promise.resolve(summaryCalls === 1
      ? new Response(JSON.stringify({ isAvailable: true, pageViews: 24, sessions: 3, sessionConversionRate: 1.5 }))
      : new Response('', { status: 503 }));
  } });
  try {
    const modal = f.window.document.getElementById('convModal');
    modal.dispatchEvent(new f.window.Event('show.bs.modal'));
    await settle();
    const originalFetch = f.window.fetch;
    let resolveConversions;
    f.window.fetch = (url, options) => String(url).includes('/conversions?')
      ? new Promise(resolve => { resolveConversions = resolve; }) : originalFetch(url, options);
    f.window.document.querySelector('[data-site-key="protect"]').click();
    await settle();
    assert.equal(summaryCalls, 2);
    resolveConversions(new Response(JSON.stringify({ totalConversions: 2, recent: [] })));
    await settle();
    assert.equal(summaryCalls, 2, 'an unavailable summary needs an explicit retry');
    assert.equal(modal.querySelector('.modal-body').getAttribute('aria-busy'), 'false');
    assert.equal(modal.querySelector('.modal-body').inert, true);
    assert.equal(f.window.document.getElementById('conv-session').textContent, '—');
    assert.equal(f.window.document.getElementById('analytics-refresh').disabled, false);
    assert.equal(modal.querySelector('[data-analytics-detail-status] [data-analytics-refresh]').disabled, false);
  } finally { await f.dispose(); }
});
