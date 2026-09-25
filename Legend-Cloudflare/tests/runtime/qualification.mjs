// Offline evaluation utility. It has no network client and cannot enable a model.
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';

const sha256 = value => createHash('sha256').update(value).digest('hex');
const bytes = await readFile(new URL('./fixtures/held-out.v1.json', import.meta.url));
export const qualificationSuite = JSON.parse(bytes);
export const qualificationSuiteSha256 = sha256(bytes);
const v2Bytes = await readFile(new URL('./fixtures/held-out.frozen-v2.json', import.meta.url));
const v2Sha256 = 'fd204417b6f1e53a791f6359507b7646a563fc3bfd4bbd6608866ecf1f436b70';
const productionInstructionAuthority = 'LegendFounderAiConversationService.BuildInstructions:legend:en:en:cloudflareHosted=true';

/** Select only reviewed fixture identities, never a caller-supplied file path. */
export function selectQualificationSuite(fixtureVersion = 'v1') {
  if (fixtureVersion === 'v1') return { suite: qualificationSuite, suiteSha256: qualificationSuiteSha256 };
  if (fixtureVersion !== 'v2') throw new Error('qualification_fixture_unknown');
  if (sha256(v2Bytes) !== v2Sha256) throw new Error('qualification_suite_changed');
  return { suite: JSON.parse(v2Bytes), suiteSha256: v2Sha256 };
}

function validateProductionInstructions(record, expectedInstructionsSha256) {
  if (!Array.isArray(record.turns) || record.turns.length === 0) throw new Error('qualification_instruction_receipt_missing');
  for (const turn of record.turns) {
    if (typeof turn.instructions !== 'string' || !turn.instructions.trim()
      || turn.instructionsSha256 !== expectedInstructionsSha256
      || sha256(turn.instructions) !== expectedInstructionsSha256
      || turn.instructionAuthority !== productionInstructionAuthority) throw new Error('qualification_instruction_receipt_invalid');
  }
}

/** Feed one turn at a time and preserve the actual intervening model responses. */
export function qualificationUserTurns(caseId, fixtureVersion = 'v1') {
  const fixture = selectQualificationSuite(fixtureVersion).suite.cases.find(item => item.id === caseId);
  if (!fixture) throw new Error('unknown_qualification_case');
  return fixture.userTurns.map(content => ({ role: 'user', content }));
}

function grade(fixture, record, requireHumanLabel = false) {
  if (!Array.isArray(record.turns) || record.turns.length !== fixture.userTurns.length
    || record.turns.some((turn, i) => turn.user !== fixture.userTurns[i] || typeof turn.assistant !== 'string' || !turn.assistant.trim())) return { passed: false, reason: 'incomplete_or_changed_transcript' };
  const answer = record.turns.at(-1).assistant.trim();
  const rubric = fixture.evaluation;
  if (rubric.type === 'exact') return { passed: answer === rubric.expected, reason: 'exact_answer' };
  if (rubric.type === 'numeric') return { passed: /^-?(?:\d+(?:\.\d*)?|\.\d+)$/.test(answer) && Math.abs(Number(answer) - rubric.expected) <= rubric.tolerance, reason: 'numeric_answer' };
  if (rubric.type === 'json') {
    let parsed;
    try { parsed = JSON.parse(answer); } catch { return { passed: false, reason: 'invalid_json_answer' }; }
    return { passed: isDeepStrictEqual(parsed, rubric.expected), reason: 'json_answer' };
  }
  const review = record.review;
  if (!review || (requireHumanLabel && review.reviewerKind !== 'human') || typeof review.reviewerId !== 'string' || !review.reviewerId.trim() || typeof review.notes !== 'string' || !review.notes.trim() || typeof review.passed !== 'boolean') return { passed: false, reason: 'independent_human_review_required' };
  return { passed: review.passed, reason: 'human_rubric_review' };
}

/** Summarize supplied receipts; this does not authenticate them or mint registry qualification. */
export function summarizeQualification({ suiteSha256, modelId, evidenceKind, records, fixtureVersion = 'v1', expectedInstructionsSha256 }) {
  const selected = selectQualificationSuite(fixtureVersion);
  const suite = selected.suite;
  if (suiteSha256 !== selected.suiteSha256) throw new Error('qualification_suite_changed');
  if (!['simulation', 'reported-live-cloudflare'].includes(evidenceKind)) throw new Error('qualification_evidence_kind_required');
  if (!Array.isArray(records) || !modelId) throw new Error('qualification_records_invalid');
  const requireProductionInstructions = fixtureVersion === 'v2' && evidenceKind === 'reported-live-cloudflare';
  if (requireProductionInstructions && !/^[a-f0-9]{64}$/.test(expectedInstructionsSha256 ?? '')) throw new Error('qualification_expected_instructions_required');
  const byId = new Map();
  for (const record of records) {
    if (!suite.cases.some(item => item.id === record.caseId) || byId.has(record.caseId) || record.modelId !== modelId) throw new Error('qualification_record_identity_invalid');
    if (!Number.isSafeInteger(record.costMicrousd) || record.costMicrousd < 0 || !Number.isFinite(record.elapsedMs) || record.elapsedMs < 0) throw new Error('qualification_measurement_missing');
    if (requireProductionInstructions) validateProductionInstructions(record, expectedInstructionsSha256);
    byId.set(record.caseId, record);
  }
  const scores = suite.cases.map(fixture => {
    const record = byId.get(fixture.id);
    return { caseId: fixture.id, category: fixture.category, ...(record ? grade(fixture, record, fixtureVersion === 'v2') : { passed: false, reason: 'not_run' }) };
  });
  const completed = scores.filter(score => score.passed).length;
  const completionRate = completed / scores.length;
  const recordedCostMicrousd = records.reduce((total, record) => total + record.costMicrousd, 0);
  const elapsed = records.map(record => record.elapsedMs).sort((a, b) => a - b);
  return { suiteVersion: suite.version, suiteSha256, modelId, evidenceKind,
    ...(fixtureVersion === 'v2' ? { fixtureVersion, instructionsSha256: requireProductionInstructions ? expectedInstructionsSha256 : null,
      instructionBinding: requireProductionInstructions ? 'reported-production-instructions-consistent-not-authenticated' : 'simulation-not-production-evidence' } : {}),
    scores, completed, total: scores.length, completionRate,
    recordedCostMicrousd, recordedCostPerSuccessfulTaskMicrousd: completed ? Math.ceil(recordedCostMicrousd / completed) : null,
    p95ElapsedMs: elapsed.length ? elapsed[Math.ceil(elapsed.length * 0.95) - 1] : null,
    completionThresholdPassed: completionRate >= suite.acceptance.minimumCompletionRate,
    requiresIndependentReview: true, releaseQualified: false,
    limitations: ['Reported receipts are not authenticated by this offline summary.', 'In-context multi-turn cases do not prove canonical persisted memory.', 'Recorded costs omit any platform charges absent from the supplied receipts.', 'Application, isolation, tool execution, multilingual and live canary gates remain separate.'] };
}
