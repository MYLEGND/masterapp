import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
async function fixture({ device = true, founder = false, siteKey = 'legend', selectedAgent = false, business = false, global = false, performanceHandler = null, captureDeadlines = false } = {}) {
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
    <div id="kpi-pageviews"></div><div id="kpi-session"></div>
    <button id="channel-performance-refresh"></button><div id="channel-performance-grid"></div><div id="growth-economics-grid"></div><div id="growth-economics-spend"></div>
    ${device ? '<button id="mod-device-intelligence"></button><div id="deviceIntelligenceModal"></div><div id="deviceIntelligenceContent"></div><div id="deviceSessions"></div>' : ''}`,
    { url: 'https://portal.example.test/WebsiteAnalytics', runScripts: 'outside-only' });
  const { window } = dom;
  await new Promise(resolve => window.document.addEventListener('DOMContentLoaded', resolve, { once: true }));
  const calls = [], errors = [], polls = [], deadlines = [];
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
  window.fetch = async (url, options = {}) => {
    calls.push(String(url));
    const path = new URL(url, window.location.href).pathname;
    if (path.endsWith('/marketing-manager/performance') && performanceHandler) return performanceHandler(url, options);
    const data = path.endsWith('/summary') ? { isAvailable: true, scopeLabel: 'Founder Personal', pageViews: 7, uniqueVisitors: 2, sessions: 2, verifiedLeads: 0, sessionConversionRate: 0 }
      : path.endsWith('/DeviceIntelligence') ? { sessions: 2, events: 7 }
      : path.endsWith('/marketing-manager/performance') ? { channels: [], economics: { totalMarketingSpend: 12, channels: [] } }
      : { channels: [] };
    return new Response(JSON.stringify(data), { headers: { 'content-type': 'application/json' } });
  };
  window.eval(source);
  window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
  await new Promise(resolve => setTimeout(resolve, 30));
  return { dom, window, calls, errors, copied, polls, deadlines };
}

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
    } finally { f.dom.window.close(); }
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
    } finally { f.dom.window.close(); }
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


test('refresh removes prior totals and a scope change ignores an older response arriving last', async () => {
  const f = await fixture({ founder: true });
  try {
    const pending = [];
    const originalFetch = f.window.fetch;
    f.window.fetch = (url, options) => String(url).includes('/marketing-manager/performance')
      ? new Promise(resolve => pending.push({ resolve, signal: options.signal }))
      : originalFetch(url, options);
    const refresh = f.window.document.getElementById('channel-performance-refresh');
    refresh.click();
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
    f.window.document.querySelector('[data-site-key="protect"]').click();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(pending[0].signal.aborted, true);
    const response = spend => new Response(JSON.stringify({ channels: [], economics: { totalMarketingSpend: spend, channels: [] } }));
    pending[1].resolve(response(24));
    await new Promise(resolve => setTimeout(resolve, 10));
    pending[0].resolve(response(99));
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /24/);
    f.window.fetch = async () => new Response('', { status: 503 });
    refresh.click();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
  } finally { f.dom.window.close(); }
});

test('repeated summary polls reuse a delayed same-scope channel read until it renders', async () => {
  const pending = [];
  const f = await fixture({ founder: true, performanceHandler: (url, options) => new Promise(resolve => pending.push({ resolve, signal: options.signal })) });
  try {
    assert.equal(pending.length, 1);
    for (let i = 0; i < 3; i++) await f.polls[0]();
    assert.equal(pending.length, 1);
    assert.equal(pending[0].signal.aborted, false);
    pending[0].resolve(new Response(JSON.stringify({ channels: [], economics: { totalMarketingSpend: 42, channels: [] } })));
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /42/);
    assert.match(f.window.document.getElementById('channel-performance-grid').textContent, /No channel outcomes/);
  } finally { f.dom.window.close(); }
});

test('a channel request deadline remains unavailable and releases the in-flight read', async () => {
  const pending = [];
  const f = await fixture({ founder: true, captureDeadlines: true, performanceHandler: (url, options) => new Promise((resolve, reject) => {
    pending.push({ resolve, signal: options.signal });
    options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
  }) });
  try {
    assert.equal(f.deadlines.length, 1);
    f.deadlines[0]();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.match(f.window.document.getElementById('channel-performance-grid').textContent, /timed out/);
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
    await f.polls[0]();
    assert.equal(pending.length, 2);
    assert.equal(pending[1].signal.aborted, false);
  } finally { f.dom.window.close(); }
});
