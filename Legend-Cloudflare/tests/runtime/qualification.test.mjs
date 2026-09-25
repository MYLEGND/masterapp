import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { selectQualificationSuite, qualificationSuite, qualificationSuiteSha256, qualificationUserTurns, summarizeQualification } from './qualification.mjs';

// These tests validate evaluation mechanics only. No answers below come from an engine.
function simulationRecord(fixture, answer) {
  return { caseId: fixture.id, modelId: 'simulation-only', turns: fixture.userTurns.map((user, index) => ({ user, assistant: index === fixture.userTurns.length - 1 ? answer : 'Simulation acknowledgment.' })), elapsedMs: 10, costMicrousd: 2 };
}
function summarize(records, extra = {}) { return summarizeQualification({ suiteSha256: qualificationSuiteSha256, modelId: 'simulation-only', evidenceKind: 'simulation', records, ...extra }); }

test('held-out suite covers reasoning, multi-turn state and code diagnosis before any live evaluation', () => {
  assert.equal(qualificationSuite.status, 'prepared-unrun'); assert.equal(qualificationSuite.cases.length, 12);
  for (const category of ['reasoning', 'multiturn', 'code_diagnosis']) assert.equal(qualificationSuite.cases.filter(item => item.category === category).length, 4);
  assert.equal(qualificationSuite.acceptance.minimumCompletionRate, 0.9);
});
test('qualification prompt projection excludes scoring rubrics and expected answers', () => {
  for (const fixture of qualificationSuite.cases) {
    const turns = qualificationUserTurns(fixture.id);
    assert.deepEqual(turns.map(turn => turn.content), fixture.userTurns);
    assert(turns.every(turn => Object.keys(turn).join(',') === 'role,content' && turn.role === 'user'));
  }
});
test('unrun cases count as failures and fixture edits invalidate prior evidence', () => {
  const result = summarize([]); assert.equal(result.completed, 0); assert.equal(result.total, 12); assert.equal(result.completionThresholdPassed, false);
  assert.throws(() => summarize([], { suiteSha256: 'old-suite' }), /qualification_suite_changed/);
});
test('an exact-answer fixture grades external text without invoking inference', () => {
  const fixture = qualificationSuite.cases.find(item => item.id === 'reasoning-count');
  assert.equal(summarize([simulationRecord(fixture, '113')]).completed, 1);
  assert.equal(summarize([simulationRecord(fixture, '114')]).completed, 0);
});
test('JSON state correction grades actual final reply and requires every preceding turn', () => {
  const fixture = qualificationSuite.cases.find(item => item.id === 'multiturn-correction');
  const record = simulationRecord(fixture, '{"cabinetColor":"teal","deliveryLabel":"Q7M"}');
  assert.equal(summarize([record]).completed, 1);
  record.turns.shift(); assert.equal(summarize([record]).completed, 0);
});
test('diagnostic quality requires a documented independent rubric review', () => {
  const fixture = qualificationSuite.cases.find(item => item.id === 'code-tenant-filter');
  const record = simulationRecord(fixture, 'simulated diagnostic answer');
  assert.equal(summarize([record]).completed, 0);
  record.review = { reviewerId: 'simulation-reviewer', notes: 'Fixture-only scoring test, not a live model judgment.', passed: true };
  const report = summarize([record]); assert.equal(report.completed, 1); assert.equal(report.releaseQualified, false); assert.equal(report.requiresIndependentReview, true);
});
test('duplicate case receipts and substituted models cannot inflate reported success', () => {
  const record = simulationRecord(qualificationSuite.cases[0], '113');
  assert.throws(() => summarize([record, record]), /qualification_record_identity_invalid/);
  assert.throws(() => summarize([{ ...record, modelId: 'substituted' }]), /qualification_record_identity_invalid/);
});


const v2 = selectQualificationSuite('v2');
function summarizeV2(records, extra = {}) {
  return summarizeQualification({ fixtureVersion: 'v2', suiteSha256: v2.suiteSha256,
    modelId: 'simulation-only', evidenceKind: 'simulation', records, ...extra });
}
// Synthetic instruction text exercises consistency checks; it is not production evidence.
const simulatedInstructions = 'SIMULATION: production instruction binding mechanics only.';
const simulatedInstructionsSha256 = createHash('sha256').update(simulatedInstructions).digest('hex');
function liveShapedRecord() {
  const record = simulationRecord(v2.suite.cases[0], '2');
  record.turns = record.turns.map(turn => ({ ...turn, instructions: simulatedInstructions,
    instructionsSha256: simulatedInstructionsSha256,
    instructionAuthority: 'LegendFounderAiConversationService.BuildInstructions:legend:en:en:cloudflareHosted=true' }));
  return record;
}
function summarizeLiveShaped(records, extra = {}) {
  return summarizeV2(records, { evidenceKind: 'reported-live-cloudflare',
    expectedInstructionsSha256: simulatedInstructionsSha256, ...extra });
}

test('v1 remains the historical default and v2 selection is explicit and pinned', () => {
  assert.equal(selectQualificationSuite().suite, qualificationSuite);
  assert.equal(v2.suiteSha256, 'fd204417b6f1e53a791f6359507b7646a563fc3bfd4bbd6608866ecf1f436b70');
  assert.equal(v2.suite.version, 'legend-held-out.2026-09-18.2');
  assert.equal(summarizeV2([]).completed, 0);
  assert.throws(() => summarize([], { fixtureVersion: 'v2' }), /qualification_suite_changed/);
  assert.throws(() => summarizeV2([], { fixtureVersion: '../fixtures/held-out.v1.json' }), /qualification_fixture_unknown/);
  assert(!Object.hasOwn(summarize([]), 'instructionBinding'));
});
test('v2 fixture file mutation is rejected without changing the v1 default', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'legend-qualification-'));
  try {
    await mkdir(join(dir, 'fixtures'));
    await writeFile(join(dir, 'qualification.mjs'), await readFile(new URL('./qualification.mjs', import.meta.url)));
    await writeFile(join(dir, 'fixtures/held-out.v1.json'), await readFile(new URL('./fixtures/held-out.v1.json', import.meta.url)));
    await writeFile(join(dir, 'fixtures/held-out.frozen-v2.json'), JSON.stringify({ ...v2.suite, version: 'tampered' }));
    const modified = await import(pathToFileURL(join(dir, 'qualification.mjs')));
    assert.equal(modified.selectQualificationSuite().suiteSha256, qualificationSuiteSha256);
    assert.throws(() => modified.selectQualificationSuite('v2'), /qualification_suite_changed/);
  } finally { await rm(dir, { recursive: true, force: true }); }
});
test('v2 prompt projection excludes evaluation and rejects a v1 case identity', () => {
  for (const fixture of v2.suite.cases) {
    assert.deepEqual(qualificationUserTurns(fixture.id, 'v2'), fixture.userTurns.map(content => ({ role: 'user', content })));
  }
  assert.throws(() => qualificationUserTurns(qualificationSuite.cases[0].id, 'v2'), /unknown_qualification_case/);
  assert.throws(() => summarizeV2([simulationRecord(qualificationSuite.cases[0], '113')]), /qualification_record_identity_invalid/);
});
test('v2 keeps strict raw JSON without fence stripping or extra properties', () => {
  const fixture = v2.suite.cases.find(item => item.evaluation.type === 'json');
  const answer = JSON.stringify(fixture.evaluation.expected);
  assert.equal(summarizeV2([simulationRecord(fixture, answer)]).completed, 1);
  for (const wrong of ['```json\n' + answer + '\n```', JSON.stringify({ ...fixture.evaluation.expected, fabricated: true })]) {
    assert.equal(summarizeV2([simulationRecord(fixture, wrong)]).completed, 0);
  }
});
test('v2 AI review cannot satisfy required human rubric review', () => {
  const fixture = v2.suite.cases.find(item => item.evaluation.type === 'human');
  assert(fixture);
  const record = simulationRecord(fixture, 'Synthetic qualitative response.');
  for (const reviewerKind of [undefined, 'ai', 'independent-ai']) {
    record.review = { reviewerKind, reviewerId: 'synthetic-reviewer', notes: 'Scorer test only.', passed: true };
    assert.equal(summarizeV2([record]).completed, 0);
  }
  record.review.reviewerKind = 'human';
  const result = summarizeV2([record]);
  assert.equal(result.completed, 1);
  assert.equal(result.releaseQualified, false);
  assert.equal(result.requiresIndependentReview, true);
});
test('v2 live-shaped receipts bind exact instruction bytes and retain non-authentication limitation', () => {
  const result = summarizeLiveShaped([liveShapedRecord()]);
  assert.equal(result.completed, 1);
  assert.equal(result.instructionsSha256, simulatedInstructionsSha256);
  assert.equal(result.instructionBinding, 'reported-production-instructions-consistent-not-authenticated');
  assert.equal(result.releaseQualified, false);
  assert.throws(() => summarizeLiveShaped([], { expectedInstructionsSha256: undefined }), /qualification_expected_instructions_required/);
  assert.throws(() => summarizeLiveShaped([], { expectedInstructionsSha256: 'not-a-hash' }), /qualification_expected_instructions_required/);
});
test('v2 live-shaped receipts reject absent, changed, mixed or falsely attributed instructions', () => {
  for (const change of [
    { instructions: undefined }, { instructions: simulatedInstructions + ' modified' },
    { instructionsSha256: 'a'.repeat(64) }, { instructionAuthority: 'synthetic-authority' }
  ]) {
    const record = liveShapedRecord();
    Object.assign(record.turns[0], change);
    assert.throws(() => summarizeLiveShaped([record]), /qualification_instruction_receipt_invalid/);
  }
  const record = liveShapedRecord();
  const fixture = v2.suite.cases.find(item => item.userTurns.length > 1);
  record.caseId = fixture.id;
  record.turns = fixture.userTurns.map(user => ({ ...record.turns[0], user }));
  record.turns.at(-1).instructions = 'Different instructions only on final turn';
  assert.throws(() => summarizeLiveShaped([record]), /qualification_instruction_receipt_invalid/);
});
