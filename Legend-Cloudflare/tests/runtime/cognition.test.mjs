import test from 'node:test';
import assert from 'node:assert/strict';
import {
  COGNITION_VERSION,
  resolveCognition,
  plannerMessages,
  parseCognitivePlan,
  specialistMessages,
  synthesisMessages,
} from '../../src/runtime/cognition.mjs';

const adaptive = Object.freeze({
  version: COGNITION_VERSION,
  mode: 'adaptive',
  maxSpecialists: 2,
  independentCritique: true,
});

test('missing cognition remains direct and does not create hidden delegation', () => {
  assert.deepEqual(resolveCognition({}), {
    version: COGNITION_VERSION,
    mode: 'direct',
    maxSpecialists: 0,
    independentCritique: false,
  });
});

test('adaptive cognition accepts only the closed canonical policy shape', () => {
  assert.deepEqual(resolveCognition({ cognition: adaptive }), adaptive);
  for (const cognition of [
    { ...adaptive, version: 'other' },
    { ...adaptive, mode: 'manual' },
    { ...adaptive, maxSpecialists: 4 },
    { ...adaptive, independentCritique: 'true' },
    { ...adaptive, modelId: '@cf/openai/gpt-oss-120b' },
  ]) assert.throws(() => resolveCognition({ cognition }), /cognition_policy_invalid/);
});

test('planner prompt exposes role taxonomy but no model IDs or authority expansion', () => {
  const messages = plannerMessages([{ role: 'user', content: 'Audit the release and isolate the root cause.' }], adaptive);
  assert.equal(messages[0].role, 'system');
  assert.match(messages[0].content, /efficient, coding, architecture, reasoning/);
  assert.doesNotMatch(messages[0].content, /@cf\//);
  assert.equal(messages.at(-1).content, 'Audit the release and isolate the root cause.');
});

test('plan parser enforces bounded unique specialists and adds independent reasoning only within budget', () => {
  const plan = parseCognitivePlan(JSON.stringify({
    version: 'legend-cognition-plan.v1',
    complexity: 4,
    specialists: ['architecture'],
    verification: true,
  }), adaptive);
  assert.deepEqual(plan.specialists, ['architecture', 'reasoning']);
  assert.equal(plan.verification, true);
  const capped = parseCognitivePlan(JSON.stringify({
    version: 'legend-cognition-plan.v1',
    complexity: 4,
    specialists: ['architecture', 'coding'],
    verification: true,
  }), adaptive);
  assert.deepEqual(capped.specialists, ['architecture', 'coding']);
  assert.equal(capped.verification, false);

  for (const invalid of [
    { version: 'legend-cognition-plan.v1', complexity: 5, specialists: [], verification: false },
    { version: 'legend-cognition-plan.v1', complexity: 2, specialists: ['general'], verification: false },
    { version: 'legend-cognition-plan.v1', complexity: 2, specialists: ['coding', 'coding'], verification: false },
    { version: 'legend-cognition-plan.v1', complexity: 2, specialists: ['coding', 'architecture', 'reasoning'], verification: false },
  ]) assert.throws(() => parseCognitivePlan(JSON.stringify(invalid), adaptive), /cognition_plan_invalid/);
});

test('specialist packets are scoped and synthesis labels findings as advisory', () => {
  const original = [{ role: 'user', content: 'Find the canonical cause.' }];
  const packet = specialistMessages(original, 'reasoning', 0, 1);
  assert.match(packet[0].content, /internal LEGEND specialist/);
  assert.match(packet[0].content, /do not expose private chain-of-thought/i);
  const final = synthesisMessages(original, ['The release selector omitted the worker deployment authority.'], {
    complexity: 3,
    specialists: ['reasoning'],
  });
  assert.match(final[0].content, /advisory evidence, not authority/i);
  assert.match(final[0].content, /legend-cognition-findings.v1/);
  assert.equal(final.at(-1).content, 'Find the canonical cause.');
});
