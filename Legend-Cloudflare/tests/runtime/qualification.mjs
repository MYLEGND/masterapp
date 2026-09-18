// Offline evaluation utility. It has no network client and cannot enable a model.
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';

const bytes = await readFile(new URL('./fixtures/held-out.v1.json', import.meta.url));
export const qualificationSuite = JSON.parse(bytes);
export const qualificationSuiteSha256 = createHash('sha256').update(bytes).digest('hex');

/** Feed one turn at a time and preserve the actual intervening model responses. */
export function qualificationUserTurns(caseId) {
  const fixture = qualificationSuite.cases.find(item => item.id === caseId);
  if (!fixture) throw new Error('unknown_qualification_case');
  return fixture.userTurns.map(content => ({ role: 'user', content }));
}

function grade(fixture, record) {
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
  if (!review || typeof review.reviewerId !== 'string' || !review.reviewerId.trim() || typeof review.notes !== 'string' || !review.notes.trim() || typeof review.passed !== 'boolean') return { passed: false, reason: 'independent_human_review_required' };
  return { passed: review.passed, reason: 'human_rubric_review' };
}

/** Summarize supplied receipts; this does not authenticate them or mint registry qualification. */
export function summarizeQualification({ suiteSha256, modelId, evidenceKind, records }) {
  if (suiteSha256 !== qualificationSuiteSha256) throw new Error('qualification_suite_changed');
  if (!['simulation', 'reported-live-cloudflare'].includes(evidenceKind)) throw new Error('qualification_evidence_kind_required');
  if (!Array.isArray(records) || !modelId) throw new Error('qualification_records_invalid');
  const byId = new Map();
  for (const record of records) {
    if (!qualificationSuite.cases.some(item => item.id === record.caseId) || byId.has(record.caseId) || record.modelId !== modelId) throw new Error('qualification_record_identity_invalid');
    if (!Number.isSafeInteger(record.costMicrousd) || record.costMicrousd < 0 || !Number.isFinite(record.elapsedMs) || record.elapsedMs < 0) throw new Error('qualification_measurement_missing');
    byId.set(record.caseId, record);
  }
  const scores = qualificationSuite.cases.map(fixture => {
    const record = byId.get(fixture.id);
    return { caseId: fixture.id, category: fixture.category, ...(record ? grade(fixture, record) : { passed: false, reason: 'not_run' }) };
  });
  const completed = scores.filter(score => score.passed).length;
  const completionRate = completed / scores.length;
  const recordedCostMicrousd = records.reduce((total, record) => total + record.costMicrousd, 0);
  const elapsed = records.map(record => record.elapsedMs).sort((a, b) => a - b);
  return { suiteVersion: qualificationSuite.version, suiteSha256, modelId, evidenceKind,
    scores, completed, total: scores.length, completionRate,
    recordedCostMicrousd, recordedCostPerSuccessfulTaskMicrousd: completed ? Math.ceil(recordedCostMicrousd / completed) : null,
    p95ElapsedMs: elapsed.length ? elapsed[Math.ceil(elapsed.length * 0.95) - 1] : null,
    completionThresholdPassed: completionRate >= qualificationSuite.acceptance.minimumCompletionRate,
    requiresIndependentReview: true, releaseQualified: false,
    limitations: ['Reported receipts are not authenticated by this offline summary.', 'In-context multi-turn cases do not prove canonical persisted memory.', 'Recorded costs omit any platform charges absent from the supplied receipts.', 'Application, isolation, tool execution, multilingual and live canary gates remain separate.'] };
}
