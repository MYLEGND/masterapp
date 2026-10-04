import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';

const source = readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
async function fixture({ device = true } = {}) {
  const dom = new JSDOM(`<!doctype html><div class="fa-shell" data-caller-profile-id="founder-profile" data-initial-scope-profile-id="founder-profile" data-initial-scope-label="Founder Personal"></div>
    <div id="kpi-pageviews"></div><div id="kpi-session"></div>
    <button id="channel-performance-refresh"></button><div id="channel-performance-grid"></div><div id="growth-economics-grid"></div><div id="growth-economics-spend"></div>
    ${device ? '<button id="mod-device-intelligence"></button><div id="deviceIntelligenceModal"></div><div id="deviceIntelligenceContent"></div><div id="deviceSessions"></div>' : ''}`,
    { url: 'https://portal.example.test/WebsiteAnalytics', runScripts: 'outside-only' });
  const { window } = dom;
  await new Promise(resolve => window.document.addEventListener('DOMContentLoaded', resolve, { once: true }));
  const calls = [], errors = [];
  window.console.error = (...args) => errors.push(args.map(String).join(' '));
  window.console.warn = () => {};
  window.bootstrap = { Modal: { getOrCreateInstance: () => ({ show() {}, hide() {} }) } };
  window.fetch = async url => {
    calls.push(String(url));
    const path = new URL(url, window.location.href).pathname;
    const data = path.endsWith('/summary') ? { isAvailable: true, scopeLabel: 'Founder Personal', pageViews: 7, uniqueVisitors: 2, sessions: 2, verifiedLeads: 0, sessionConversionRate: 0 }
      : path.endsWith('/DeviceIntelligence') ? { sessions: 2, events: 7 }
      : path.endsWith('/marketing-manager/performance') ? { channels: [], economics: { totalMarketingSpend: 12, channels: [] } }
      : { channels: [] };
    return new Response(JSON.stringify(data), { headers: { 'content-type': 'application/json' } });
  };
  window.eval(source);
  window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
  await new Promise(resolve => setTimeout(resolve, 30));
  return { dom, window, calls, errors };
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


test('refresh removes prior totals and ignores an older response arriving last', async () => {
  const f = await fixture();
  try {
    const pending = [];
    f.window.fetch = () => new Promise(resolve => pending.push(resolve));
    const refresh = f.window.document.getElementById('channel-performance-refresh');
    refresh.click();
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
    refresh.click();
    const response = spend => new Response(JSON.stringify({ channels: [], economics: { totalMarketingSpend: spend, channels: [] } }));
    pending[1](response(24));
    await new Promise(resolve => setTimeout(resolve, 10));
    pending[0](response(99));
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.match(f.window.document.getElementById('growth-economics-spend').textContent, /24/);
    f.window.fetch = async () => new Response('', { status: 503 });
    refresh.click();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(f.window.document.getElementById('growth-economics-spend').textContent, 'Unavailable');
  } finally { f.dom.window.close(); }
});
