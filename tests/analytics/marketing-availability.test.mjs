import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../../AgentPortal/wwwroot/js/website-analytics.js', import.meta.url), 'utf8');
const start = source.indexOf('  function marketingManagerNumber(value)');
const script = source.slice(start, source.indexOf('  function channelLabel', start));
const context = {};
vm.runInNewContext(script, context);
test('unknown and nonfinite measurements display Unavailable', () => {
  for (const value of [null, undefined, NaN, Infinity, -Infinity, 'invalid'])
    assert.equal(context.marketingManagerNumber(value), 'Unavailable');
});
test('known numeric zero and nonzero measurements stay numeric', () => {
  for (const value of [0, '0']) assert.equal(context.marketingManagerNumber(value), '0');
  assert.equal(context.marketingManagerNumber(12), '12');
});
