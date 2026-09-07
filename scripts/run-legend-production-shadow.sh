#!/usr/bin/env bash
# PR #31's isolated observation runner. Non-release evidence only; no replay,
# convergence, production chat persistence, deployment, migration or Azure control plane.
set -Eeuo pipefail

readonly root="${GITHUB_WORKSPACE:-$PWD}/diagnostics/legend-shadow"
export LEGEND_VALIDATION_SCOPE="${LEGEND_VALIDATION_SCOPE:-canonical_matrix}"
case "$LEGEND_VALIDATION_SCOPE" in
  canonical_matrix)
    readonly test_name='AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyNativeProofMatrix'
    readonly stage_budget=720
    export LEGEND_PRODUCTION_PROOF_REQUIRED=true
    export LEGEND_PRODUCTION_ISOLATED_SELECT_ONLY=true
    export LEGEND_PRODUCTION_PROOF_MATRIX_VERSION=lai-027-029-v1
    ;;
  observation)
    readonly test_name='AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCandidateObservation'
    readonly stage_budget=180
    ;;
  *) echo 'Unrecognized isolated validation scope.'; exit 2 ;;
esac
mkdir -p "$root/private"
export LEGEND_VALIDATION_RESULT_PATH="$root/observation.json"
export LEGEND_VALIDATION_RUN_IDENTITY="${GITHUB_RUN_ID:-local}:${GITHUB_RUN_ATTEMPT:-0}:${LEGEND_VALIDATION_NONCE:?missing run nonce}"
readonly started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
readonly started_seconds="$(date +%s)"
status='failed'
failure='preflight_incomplete'

write_summary() {
  STATUS="$status" FAILURE="$failure" STARTED="$started" STARTED_SECONDS="$started_seconds" \
  ROOT="$root" python3 - <<'PY'
import datetime, json, os, time
from pathlib import Path
root = Path(os.environ['ROOT'])
summary = {
    'Authority': 'non-authoritative', 'DeployedSha': 'unavailable',
    'CandidateSha': os.environ.get('LEGEND_VALIDATION_CANDIDATE_SHA'),
    'WorkflowSha': os.environ.get('LEGEND_VALIDATION_WORKFLOW_SHA'),
    'RunIdentity': os.environ.get('LEGEND_VALIDATION_RUN_IDENTITY'),
    'Status': os.environ['STATUS'], 'FailureCode': os.environ['FAILURE'],
    'ValidationScope': os.environ['LEGEND_VALIDATION_SCOPE'],
    'StartedUtc': os.environ['STARTED'],
    'CompletedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'ElapsedSeconds': int(time.time()) - int(os.environ['STARTED_SECONDS']),
    'Coverage': (['exact_endpoint', 'held_out_paraphrase', 'discourse', 'cross_family_negative',
                  'deduction', 'uncertainty', 'diagnosis', 'planning', 'audience_constraints',
                  'language_routing', 'native_only_isolation']
                 if os.environ['LEGEND_VALIDATION_SCOPE'] == 'canonical_matrix' else
                 ['learning', 'machine-learning-lifecycle', 'governed_cohort',
                  'held-out-competing-hypotheses', 'held-out-discriminating-check', 'native-only-provider-isolation']),
    'ReleaseProof': False,
}
(root / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
PY
}
trap write_summary EXIT

failure='candidate_sha_missing_or_invalid'
[[ "${LEGEND_VALIDATION_CANDIDATE_SHA:-}" =~ ^[0-9a-f]{40}$ ]]
failure='candidate_source_mismatch'
[[ "$(git rev-parse HEAD)" == "$LEGEND_VALIDATION_CANDIDATE_SHA" ]]
git diff --quiet HEAD --
failure='not_configured_select_only_credential'
[[ -n "${LEGEND_PRODUCTION_READONLY_CONNECTION:-}" ]]
failure='not_configured_founder_identity'
[[ -n "${LEGEND_PRODUCTION_READONLY_FOUNDER_OID:-}" ]]
failure='required_observation_not_enabled'
[[ "${LEGEND_PRODUCTION_OBSERVATION_REQUIRED:-}" == 'true' ]]
failure='provider_credential_present'
[[ -z "${OPENAI_API_KEY:-}" && -z "${OpenAI__ApiKey:-}" ]]
failure='stale_observation_evidence'
[[ ! -e "$LEGEND_VALIDATION_RESULT_PATH" && ! -e "$root/private/observation.trx" ]]

failure='exact_test_discovery_failed'
timeout --kill-after=5s 45s dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj \
  -c Release --no-build --nologo --list-tests --filter "FullyQualifiedName=$test_name" \
  > "$root/private/discovery.log" 2>&1
TEST_NAME="$test_name" ROOT="$root" python3 - <<'PY'
import os
from pathlib import Path
lines = (Path(os.environ['ROOT']) / 'private/discovery.log').read_text(encoding='utf-8-sig').splitlines()
assert sum(line.strip() == os.environ['TEST_NAME'] for line in lines) == 1, 'Exact test discovery count is not one'
PY

failure='observation_execution_failed'
set +e
timeout --kill-after=5s "${stage_budget}s" dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj \
  -c Release --no-build --nologo --filter "FullyQualifiedName=$test_name" \
  --results-directory "$root/private" --logger 'trx;LogFileName=observation.trx' \
  > "$root/private/execution.log" 2>&1
observation_exit=$?
set -e
if (( observation_exit == 124 || observation_exit == 137 )); then
  failure='observation_process_deadline_exceeded'
  exit 1
fi

# Validate fresh exact-test evidence even after failure, but never convert a
# failed process, absent fixture, skipped test or incomplete run to a pass.
failure='observation_evidence_invalid'
ROOT="$root" STARTED="$started" TEST_NAME="$test_name" OBSERVATION_EXIT="$observation_exit" python3 - <<'PY'
import datetime, json, os, xml.etree.ElementTree as ET
from pathlib import Path
root = Path(os.environ['ROOT'])
started = datetime.datetime.fromisoformat(os.environ['STARTED'].replace('Z', '+00:00'))
now = datetime.datetime.now(datetime.timezone.utc)
trx_path = root / 'private/observation.trx'
result_path = root / 'observation.json'
for path in (trx_path, result_path):
    assert path.is_file(), 'Required fresh evidence is missing'
    assert started.timestamp() <= path.stat().st_mtime <= now.timestamp(), 'Evidence timestamp is outside this run'
trx = ET.parse(trx_path).getroot()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def timestamp(value):
    parsed = datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
    assert parsed.tzinfo is not None, 'Evidence timestamps must include an offset'
    return parsed
run_times = trx.find('t:Times', ns)
assert run_times is not None, 'TRX execution timestamps are missing'
run_start, run_finish = timestamp(run_times.attrib['start']), timestamp(run_times.attrib['finish'])
assert started <= run_start <= run_finish <= now, 'TRX content belongs to another execution window'
counters = trx.find('t:ResultSummary/t:Counters', ns)
assert counters is not None, 'TRX counters are missing'
assert all(int(counters.attrib.get(k, '-1')) == v for k, v in
           {'total': 1, 'executed': 1, 'passed': 1, 'failed': 0, 'notExecuted': 0}.items()), 'TRX did not record exactly one passing execution'
results = trx.findall('t:Results/t:UnitTestResult', ns)
assert len(results) == 1 and results[0].attrib['outcome'] == 'Passed', 'TRX result is not a single pass'
test_start, test_finish = timestamp(results[0].attrib['startTime']), timestamp(results[0].attrib['endTime'])
assert run_start <= test_start <= test_finish <= run_finish, 'TRX test timestamps do not belong to this run'
definitions = trx.findall('t:TestDefinitions/t:UnitTest', ns)
assert len(definitions) == 1 and definitions[0].attrib['id'] == results[0].attrib['testId'], 'TRX identity mismatch'
execution = definitions[0].find('t:Execution', ns)
assert execution is not None and execution.attrib['id'] == results[0].attrib['executionId'], 'TRX execution identity mismatch'
method = definitions[0].find('t:TestMethod', ns)
assert method is not None, 'TRX method identity missing'
assert method.attrib['className'].split(',')[0] + '.' + method.attrib['name'] == os.environ['TEST_NAME'], 'TRX executed another test'
result = json.loads(result_path.read_text(encoding='utf-8-sig'))
assert result['CandidateSha'] == os.environ['LEGEND_VALIDATION_CANDIDATE_SHA']
assert result['RunIdentity'] == os.environ['LEGEND_VALIDATION_RUN_IDENTITY']
assert result['Authority'] == 'non-authoritative' and result['DeployedSha'] == 'unavailable'
assert test_start <= timestamp(result['StartedUtc']) <= timestamp(result['CompletedUtc']) <= test_finish, 'JSON evidence does not belong to the selected TRX execution'
assert result['Status'] == 'passed' and result['SqlPrincipalVerified'] is True
cases = result['CaseResults']
assert all(case['Status'] == 'passed' and case['ElapsedMilliseconds'] >= 0 for case in cases)
assert result['SelectCommandCount'] > 0
assert result['ProviderClientCount'] == result['ProviderHttpCallCount'] == 0
assert result['QueryTimeoutSeconds'] == 15
if os.environ['LEGEND_VALIDATION_SCOPE'] == 'canonical_matrix':
    assert result['MatrixVersion'] == 'lai-027-029-v1' and result['IsolatedReadOnlyMode'] is True
    expected = {'exact_endpoint', 'held_out_paraphrase', 'discourse', 'cross_family_negative',
                'deduction', 'uncertainty', 'diagnosis', 'planning', 'audience_constraints',
                'language_routing', 'native_only_isolation'}
    assert set(result['Categories']) == expected
    assert {case['Category'] for case in cases} == expected
    assert result['ExecutedCases'] == result['TotalCases'] == len(cases)
    assert 11 <= len(cases) <= 16 and result['FailedCases'] == 0
    assert result['NativePasses'] >= 1 and result['NegativePasses'] >= 1
    assert result['NativePasses'] + result['NegativePasses'] == len(cases)
    references = [case['Reference'] for case in cases]
    assert all(isinstance(reference, str) and reference for reference in references)
    assert len(set(references)) == len(references), 'Canonical matrix case references are duplicated'
    assert all(type(case['ExpectedNative']) is bool and type(case['NativeSupported']) is bool for case in cases)
    assert result['NativePasses'] == sum(case['ExpectedNative'] for case in cases)
    assert result['NegativePasses'] == sum(not case['ExpectedNative'] for case in cases)
    for case in cases:
        assert case['NativeSupported'] is case['ExpectedNative'], 'Native case outcome contradicts its pass result'
        if case['ExpectedNative']:
            assert case['EvidenceCount'] > 0 and case['ResponseAuthority'] == 'LegendAi'
            assert case['Stage'] == 'native_response'
        else:
            assert case['ResponseAuthority'] == 'SystemDiagnostic' and case['Stage'] == 'native_only_blocked'
    assert all(case['Phase'] == 'execution' and case['ProviderClientCount'] == 0 for case in cases)
    assert result['ProductionWriteCommandCount'] == result['ProductionSaveChangesAttempts'] == 0
    assert result['ObservationTimeoutSeconds'] == 600 and 0 < result['ElapsedMilliseconds'] <= 605000
else:
    assert result['Version'] == 'candidate-select-observation-v2'
    expected = {'learning', 'machine-learning-lifecycle', 'governed_cohort',
                'held-out-competing-hypotheses', 'held-out-discriminating-check', 'native-only-provider-isolation'}
    assert set(result['Coverage']) == expected
    assert result['ExecutedCases'] == len(cases) == len(expected)
    assert {case['Category'] for case in cases} == expected
    assert result['BlockedCommandCount'] == result['SaveChangesAttempts'] == 0
    native_cases = [case for case in cases if 'ExpectedNative' in case]
    assert len(native_cases) == 3 and sum(case['ExpectedNative'] is True for case in native_cases) == 2
    for case in native_cases:
        assert case['NativeSupported'] is case['ExpectedNative']
        assert case['ProviderClientCount'] == case['ProviderHttpCallCount'] == 0
        if case['ExpectedNative']:
            assert case['GraphComposed'] is True and case['UnknownComponentCount'] == 0
            assert case['EvidenceCount'] > 0 and case['NativeReason'] == 'semantic_transition_governed_composed'
            assert len(case['AnswerSha256']) == 64
    assert result['ObservationTimeoutSeconds'] == 120 and 0 < result['ElapsedMilliseconds'] <= 125000
assert int(os.environ['OBSERVATION_EXIT']) == 0, 'Test process did not pass'
PY
failure='none'
status='passed'
