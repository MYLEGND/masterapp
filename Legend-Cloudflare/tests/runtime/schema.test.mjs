import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { MODEL_REGISTRY } from '../../src/runtime/registry.mjs';
import { buildProviderInput, parseProviderResponse } from '../../src/runtime/adapter.mjs';

const evidence = JSON.parse(await readFile(new URL('./fixtures/account-schema-snapshot.json', import.meta.url)));

test('adapter request fields agree with authenticated account schema projection', () => {
  assert.equal(evidence.cli, 'wrangler@4.135.0'); assert.equal(evidence.inferencePerformed, false);
  for (const model of MODEL_REGISTRY) {
    const observed = evidence.models.find(item => item.modelId === model.id);
    assert(observed); assert.match(observed.rawSchemaSha256, /^[0-9a-f]{64}$/);
    const request = buildProviderInput(model, [{ role: 'user', content: 'schema-only fixture' }], { tools: [{ type: 'function', name: 'inspect', description: 'Read an approved source.', parameters: { type: 'object', properties: {} } }] }, 256);
    assert(Object.keys(request).every(key => observed.inputPropertyNames.includes(key)), model.id);
    assert(observed.inputRequired.every(key => Object.hasOwn(request, key)), model.id);
    assert.equal(request[model.outputLimitParameter], 256);
  }
});

test('schema-declared Qwen text completion is normalized without invented chat fields', () => {
  const model = MODEL_REGISTRY[0];
  const observed = evidence.models.find(item => item.modelId === model.id);
  assert(observed.outputBranches.some(branch => branch.choiceProperties.includes('text')));
  // SIMULATION: response shaped from the authenticated schema, never a live generation.
  const output = parseProviderResponse({ object: 'text_completion', model: model.id, choices: [{ index: 0, text: 'schema fixture', finish_reason: 'stop' }], usage: { prompt_tokens: 4, completion_tokens: 3, total_tokens: 7 } }, model);
  assert.equal(output.text, 'schema fixture'); assert.deepEqual(output.usage, { inputTokens: 4, outputTokens: 3 });
});

test('Responses output cannot publish nonterminal text as completed', () => {
  for (const status of ['queued', 'in_progress', 'failed', 'cancelled']) assert.throws(() => parseProviderResponse({ status, output: [{ type: 'message', content: [{ type: 'output_text', text: 'unfinished' }] }] }, MODEL_REGISTRY[1]), /provider_not_complete/);
});

test('schema metadata remains separate from inference qualification', () => {
  assert(evidence.models.every(item => item.rawSchemaSha256));
  assert(MODEL_REGISTRY.every(model => model.enabled === false && model.qualification === null));
});
