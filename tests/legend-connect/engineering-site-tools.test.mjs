import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../SHARED/wwwroot/js/legend-site-tools.js', import.meta.url), 'utf8');
const required = ['legend_engineering_bootstrap', 'legend_inspect_repository',
  'legend_prepare_software_repair', 'legend_engineering_renew_turn', 'legend_engineering_complete_turn'];
async function run({ supported = true, reject = null, enabled = true } = {}) {
  const status = { dataset: { enabled: String(enabled) }, textContent: enabled ? 'Unverified' : 'Paused by Founder' };
  const registered = [];
  let requests = 0;
  const names = [...Array.from({ length: 25 }, (_, i) => `read_${i}`), ...required];
  const document = {
    querySelector: () => status,
    scripts: [],
    modelContext: supported ? { async registerTool(tool) {
      if (tool.name === reject) throw new Error('Registration rejected');
      registered.push(tool);
    } } : undefined
  };
  vm.runInNewContext(source, {
    document, AbortController, URL,
    window: { addEventListener() {}, location: { origin: 'https://example.invalid' },
      async fetch() { requests++; return { ok: true, async json() { return {
        tools: names.map(name => ({ type: 'function', name, description: name, parameters: { type: 'object' } })),
        toolPolicies: Object.fromEntries(required.map(name => [name, { readOnly: false, consequential: true }]))
      }; } }; } }
  });
  await new Promise(resolve => setImmediate(resolve));
  return { status: status.textContent, registered, requests };
}

test('unsupported transport is reported without claiming Work execution', async () => {
  const result = await run({ supported: false });
  assert.equal(result.status, 'Browser tool transport unavailable');
  assert.equal(result.requests, 0);
});
test('all catalog tools register including engineering tools after index 24', async () => {
  const result = await run();
  assert.equal(result.registered.length, 30);
  assert.equal(result.status, 'Tools connected · awaiting governed task');
  assert.equal(result.registered.at(-1).annotations.consequentialHint, true);
});
test('partial registration is never presented as connected', async () => {
  assert.equal((await run({ reject: 'legend_engineering_complete_turn' })).status,
    'Engineering tool registration incomplete');
});
test('Founder pause remains visible even with registered transport', async () => {
  assert.equal((await run({ enabled: false })).status, 'Paused by Founder');
});
