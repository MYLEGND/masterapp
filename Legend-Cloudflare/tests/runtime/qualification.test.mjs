import test from 'node:test';
import assert from 'node:assert/strict';
import { qualificationSuite, qualificationSuiteSha256, qualificationUserTurns, summarizeQualification } from './qualification.mjs';

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
