import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
const start = source.indexOf('  function marketingManagerMoney(');
const script = source.slice(start, source.indexOf('  function marketingManagerNumber(', start));
const context = {};
vm.runInNewContext(script, context);
test('recorded revenue displays unknown separately from explicit zero', () => {
  assert.equal(context.marketingManagerMoney(null), 'Unavailable');
  assert.match(context.marketingManagerMoney(0), /0/);
});
test('both channel and economics views label historical amounts as recorded revenue', () => {
  assert.equal((source.match(/\['Recorded revenue', marketingManagerMoney\(row.revenue\)\]/g) || []).length, 2);
  const view = fs.readFileSync(new URL('../../AgentPortal/Views/WebsiteAnalytics/Index.cshtml', import.meta.url), 'utf8');
  assert.match(view, /<span>Recorded revenue<\/span><strong id="growth-economics-revenue">/);
});
