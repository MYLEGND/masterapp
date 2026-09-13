#!/usr/bin/env bash
# PR #31's isolated observation runner. Non-release evidence only; no replay,
# convergence, production chat persistence, deployment, migration or Azure control plane.
# The explicit provider_resources scope observes existing live provider boundaries
# with local guarded storage; it never supplies production SQL proof.
set -Eeuo pipefail

# The same authority can check input delivery before a costly build.
readonly mode="${1:-execute}"
case "$mode" in execute|--check-configuration) ;; *) echo "Unknown runner mode."; exit 2 ;; esac
export LEGEND_VALIDATION_CONFIGURATION_ONLY=false
[[ "$mode" != --check-configuration ]] || export LEGEND_VALIDATION_CONFIGURATION_ONLY=true
export LEGEND_VALIDATION_TEST_PROCESS_STARTED=false

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
  provider_resources)
    readonly test_name='AgentPortal.Tests.LegendFounderAiComprehensiveDiagnosticContractTests.ResourceEnabled_AzureBoundary_ReportsActualProviderOutcomeWithoutLearning'
    readonly stage_budget=240
    ;;
  *) echo 'Unrecognized isolated validation scope.'; exit 2 ;;
esac
test_names="$test_name"
if [[ "$LEGEND_VALIDATION_SCOPE" == 'provider_resources' ]]; then
  test_names+=$'\nAgentPortal.Tests.LegendFounderAiComprehensiveDiagnosticContractTests.ResourceEnabled_ResearchBoundary_ReportsActualGovernedOutcomeWithoutPromotion'
  test_names+=$'\nAgentPortal.Tests.LegendFounderAiModeIsolationTests.ProviderAcceptanceCanary_LiveProviderAcceptsCompleteZeroWriteCatalog'
fi
readonly test_names
readonly test_filter="FullyQualifiedName=${test_names//$'\n'/|FullyQualifiedName=}"
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
failure = os.environ['FAILURE']
# Configuration observations are presence checks only. Never serialize a
# credential, connection string, Founder identifier, or raw test exception.
def input_present(name):
    return bool(os.environ.get(name, '').strip())
configuration = {
    'ObservationScope': 'current_runner_process',
    'ProductionConfigurationStatus': 'NOT_INSPECTED',
    'CredentialStoreStatus': 'NOT_INSPECTED',
    'EffectiveProviderConfigurationStatus': 'NOT_INSPECTED',
    'InputPresence': {name: input_present(name) for name in (
        'LEGEND_PRODUCTION_READONLY_CONNECTION', 'LEGEND_PRODUCTION_READONLY_FOUNDER_OID',
        'OPENAI_API_KEY', 'OpenAI__ApiKey', 'AzureTranslator__Key',
        'AZURE_TRANSLATOR_KEY', 'AzureTranslator__Endpoint')},
    'SqlPrincipalStatus': 'NOT_VERIFIED_BY_INPUT_PRESENCE',
    'ProductionProviderParity': 'NOT_VERIFIED',
}
configuration_only = os.environ['LEGEND_VALIDATION_CONFIGURATION_ONLY'] == 'true'
process_started = os.environ['LEGEND_VALIDATION_TEST_PROCESS_STARTED'] == 'true'

resource_scope = os.environ['LEGEND_VALIDATION_SCOPE'] == 'provider_resources'
startup_diagnoses = {
    'configuration_inputs_available': (
        'INPUTS_AVAILABLE', 'configuration', 'scripts/run-legend-production-shadow.sh',
        'Required inputs reached this process. Their validity and SQL permissions remain unverified.',
        'Build the exact candidate and execute the existing guarded diagnostic.'),
    'not_configured_select_only_credential': (
        'NOT_CONFIGURED', 'configuration',
        '.github/workflows/legend-production-readonly-diagnostic.yml',
        'Reconcile the existing authorized SELECT-only credential binding with LEGEND_PRODUCTION_SELECT_ONLY_CONNECTION in LEGEND-Production-ReadOnly-Validation. This process received no usable input; existence in Azure or another environment was not inspected. Preserve the SELECT-only principal guard.',
        'Rerun this exact candidate; require successful principal verification and executed SQL cases.'),
    'not_configured_founder_identity': (
        'NOT_CONFIGURED', 'configuration',
        '.github/workflows/legend-production-readonly-diagnostic.yml',
        'Reconcile the existing Founder identity binding with LEGEND_PRODUCTION_READONLY_FOUNDER_OID in the validation environment. Missing process input does not establish a missing production Founder configuration.',
        'Rerun this exact candidate and verify the configured Founder through the existing authorization authority.'),
    'candidate_source_mismatch': (
        'IDENTITY_MISMATCH', 'candidate_identity', 'scripts/run-legend-production-shadow.sh',
        'Restore the requested clean candidate checkout and rebuild its assemblies; do not reuse another revision.',
        'Require source, assembly, workflow and diagnostic evidence identities to agree.'),
    'provider_credential_present': (
        'POLICY_CONFIGURATION', 'provider_isolation', 'scripts/run-legend-production-shadow.sh',
        'Remove external provider credentials from the native-only diagnostic environment; use the separate authorized provider acceptance check.',
        'Require zero external client construction and invocation in every native-only case.'),
    'exact_test_discovery_failed': (
        'TEST_DISCOVERY', 'test_discovery', 'AgentPortal.Tests/AgentPortal.Tests.csproj',
        'Inspect the private discovery log and restored test-runner configuration for the exact requested test.',
        'Require exactly the selected discovered and executed diagnostic entrypoints.'),
    'observation_process_deadline_exceeded': (
        'DEADLINE_EXCEEDED', 'diagnostic_execution', 'scripts/run-legend-production-shadow.sh',
        'Inspect the last recorded runtime stage and SQL duration before changing the responsible operation; a timeout alone does not identify its cause.',
        'Repeat the affected operation within the existing bounded deadline after its cause is repaired.'),
}
classification, stage, target, action, verification = startup_diagnoses.get(failure, (
    'NONE' if failure == 'none' else 'EXECUTION_OR_EVIDENCE_FAILURE',
    'completed' if failure == 'none' else 'diagnostic_execution',
    'AgentPortal.Tests/LegendFounderCurriculumSqlServerE2ETests.cs',
    'No repair indicated by this bounded run.' if failure == 'none' else
        'Inspect the matching observation.json FailureDiagnosis and DiagnosticEvidence; preserve the first failed boundary before proposing a code change.',
    'This diagnostic is not deployment approval; validate any proposed repair on the exact candidate.'
))
evidence = None
try:
    candidate = json.loads((root / 'observation.json').read_text(encoding='utf-8-sig'))
    if (isinstance(candidate, dict)
            and candidate.get('CandidateSha') == os.environ.get('LEGEND_VALIDATION_CANDIDATE_SHA')
            and candidate.get('RunIdentity') == os.environ.get('LEGEND_VALIDATION_RUN_IDENTITY')):
        evidence = candidate
except (OSError, ValueError):
    pass
if not process_started or configuration_only:
    evidence = None
resource_evidence = {}
resource_http_calls = {}
resource_reasons = {}
if resource_scope and process_started and not configuration_only:
    evidence = None
    for resource in ('azure', 'research', 'openai'):
        try:
            receipt = json.loads((root / ('resource-' + resource + '.json')).read_text(encoding='utf-8-sig'))
            if (receipt.get('CandidateSha') == os.environ.get('LEGEND_VALIDATION_CANDIDATE_SHA')
                    and receipt.get('RunIdentity') == os.environ.get('LEGEND_VALIDATION_RUN_IDENTITY')
                    and receipt.get('Resource') == resource):
                resource_reasons[resource] = receipt.get('Reason') if receipt.get('Reason') in (
                    'resource_configuration_missing', 'candidate_identity_missing', 'run_identity_missing',
                    'resource_probe_failed', 'resource_boundary_observed_not_production_data_proof') else 'unclassified'
                resource_http_calls[resource] = receipt.get('HttpCallCount') if type(receipt.get('HttpCallCount')) is int and receipt['HttpCallCount'] >= 0 else None
                resource_evidence[resource] = receipt.get('Status') if receipt.get('Status') in (
                    'OBSERVED', 'FAILED', 'NOT_CONFIGURED') else 'INVALID_EVIDENCE'
        except (OSError, ValueError, AttributeError):
            pass
    if any(value == 'NOT_CONFIGURED' for value in resource_evidence.values()):
        classification, stage = 'NOT_CONFIGURED', 'resource_prerequisite'
        target = '.github/workflows/legend-production-readonly-diagnostic.yml'
        action = 'Inspect the resource prerequisite reason before choosing a repair. For resource_configuration_missing, reconcile selected input bindings with existing authorized settings; identity failures require matching candidate/run identity. Production settings were not inspected.'
        verification = 'Rerun all three existing resource probes on this exact candidate; require actual boundary calls and zero canonical writes.'
    elif failure != 'none' and resource_evidence:
        target = 'scripts/run-legend-production-shadow.sh'
        action = 'Inspect the matching resource receipt stage, outcome and HTTP status; the observed boundary failure does not establish a code repair.'
        verification = 'Rerun the existing resource probes on the exact candidate; resource evidence cannot satisfy production SQL or deployment proof.'
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
    'RequestedCoverage': (['azure', 'research', 'openai'] if resource_scope else
                ['exact_endpoint', 'held_out_paraphrase', 'discourse', 'cross_family_negative',
                  'deduction', 'uncertainty', 'diagnosis', 'planning', 'audience_constraints',
                  'language_routing', 'native_only_isolation']
                 if os.environ['LEGEND_VALIDATION_SCOPE'] == 'canonical_matrix' else
                 ['learning', 'machine-learning-lifecycle', 'governed_cohort',
                  'held-out-competing-hypotheses', 'held-out-discriminating-check', 'native-only-provider-isolation']),
    'ReleaseProof': False,
    'ResourceReceipts': resource_evidence,
    'Configuration': configuration,
    'ConfigurationOnly': configuration_only,
    'TestProcessStarted': process_started,
    'ResourceHttpCallCounts': resource_http_calls,
    'ResourcePrerequisiteReasons': resource_reasons,
    'EvidenceValidation': 'ACCEPTED' if os.environ['STATUS'] == 'passed' else 'NOT_ACCEPTED',
    'ReceiptObservationsAreVerified': os.environ['STATUS'] == 'passed',
    'FailureDiagnosis': {
        'Classification': classification, 'ObservedStage': stage, 'ObservedReason': failure,
        'RepairTarget': target, 'RecommendedAction': action, 'NextVerification': verification,
        'RootCauseStatus': ('reported_resource_prerequisite_unavailable' if resource_scope else 'confirmed_missing_runner_input') if classification == 'NOT_CONFIGURED'
            else 'input_presence_only' if configuration_only and failure == 'configuration_inputs_available'
            else 'no_failure_observed' if failure == 'none' else 'observed_failure_only',
        'ProposedCodeFixVerified': False,
    },
    'MatchingExecutionEvidenceAvailable': bool(resource_evidence) if resource_scope else evidence is not None,
    'DetailedEvidenceFile': 'observation.json' if evidence is not None else None,
    'DetailedEvidenceFiles': ['resource-' + resource + '.json' for resource in resource_evidence],
    'ExecutedCases': (None if resource_scope and process_started else
        evidence.get('ExecutedCases') if evidence is not None else None if process_started else 0),
    'ProductionSqlExecutionVerified': bool(not resource_scope and os.environ['STATUS'] == 'passed' and evidence is not None
        and evidence.get('SqlPrincipalVerified') is True
        and isinstance(evidence.get('SelectCommandCount'), int)
        and evidence['SelectCommandCount'] > 0),
    'ResourceCoverage': {
        'ProductionSqlNative': 'NOT_EXECUTED_RESOURCE_SCOPE' if resource_scope else 'NOT_CONFIGURED' if classification == 'NOT_CONFIGURED'
            else 'SEE_MATCHING_EXECUTION_EVIDENCE' if evidence is not None else 'UNKNOWN_NO_MATCHING_EVIDENCE' if process_started else 'NOT_EXECUTED',
        'OpenAiCatalogAcceptance': resource_evidence.get('openai', 'UNKNOWN_NO_MATCHING_EVIDENCE' if process_started else 'NOT_EXECUTED') if resource_scope else 'NOT_EXECUTED_NATIVE_ONLY_SCOPE',
        'OpenAiEscalation': 'NOT_EXERCISED_BY_CATALOG_CANARY' if resource_scope else 'NOT_EXECUTED_NATIVE_ONLY_SCOPE',
        'AzureTranslation': resource_evidence.get('azure', 'UNKNOWN_NO_MATCHING_EVIDENCE' if process_started else 'NOT_EXECUTED') if resource_scope else 'NOT_EXECUTED_NATIVE_ONLY_SCOPE',
        'ExternalResearch': resource_evidence.get('research', 'UNKNOWN_NO_MATCHING_EVIDENCE' if process_started else 'NOT_EXECUTED') if resource_scope else 'NOT_EXECUTED_NATIVE_ONLY_SCOPE',
        'ProductionLearningWrites': 'NOT_EXECUTED_RESOURCE_SCOPE' if resource_scope else 'NOT_EXECUTED_SELECT_ONLY_SCOPE',
        'AuthenticatedHttp': 'NOT_EXECUTED_IN_PROCESS_SCOPE',
    },
}
(root / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
PY
}
trap write_summary EXIT

# Bash 3.2 does not apply errexit to a failed standalone [[ ... ]].
# Explicit exits keep every mandatory authority guard fail-closed on that host.
failure='candidate_sha_missing_or_invalid'
[[ "${LEGEND_VALIDATION_CANDIDATE_SHA:-}" =~ ^[0-9a-f]{40}$ ]] || exit 1
failure='candidate_source_mismatch'
[[ "$(git rev-parse HEAD)" == "$LEGEND_VALIDATION_CANDIDATE_SHA" ]] || exit 1
git diff --quiet HEAD --
if [[ "$LEGEND_VALIDATION_SCOPE" == 'provider_resources' ]]; then
  failure='required_resource_diagnostics_not_enabled'
  [[ "${LEGEND_RESOURCE_DIAGNOSTICS_REQUIRED:-}" == 'true' ]] || exit 1
else
failure='not_configured_select_only_credential'
[[ "${LEGEND_PRODUCTION_READONLY_CONNECTION:-}" =~ [^[:space:]] ]] || exit 1
failure='not_configured_founder_identity'
[[ "${LEGEND_PRODUCTION_READONLY_FOUNDER_OID:-}" =~ [^[:space:]] ]] || exit 1
failure='required_observation_not_enabled'
[[ "${LEGEND_PRODUCTION_OBSERVATION_REQUIRED:-}" == 'true' ]] || exit 1
failure='provider_credential_present'
[[ -z "${OPENAI_API_KEY:-}" && -z "${OpenAI__ApiKey:-}" ]] || exit 1
fi
if [[ "$mode" == --check-configuration ]]; then
  failure='configuration_check_requires_native_scope'
  [[ "$LEGEND_VALIDATION_SCOPE" != provider_resources ]] || exit 1
  failure='configuration_inputs_available'
  status='inputs_available'
  exit 0
fi
failure='stale_observation_evidence'
[[ ! -e "$root/private/observation.trx" ]] || exit 1
if [[ "$LEGEND_VALIDATION_SCOPE" == 'provider_resources' ]]; then
  for resource in azure research openai; do
    [[ ! -e "$root/resource-$resource.json" ]] || exit 1
  done
else
  [[ ! -e "$LEGEND_VALIDATION_RESULT_PATH" ]] || exit 1
fi

failure='exact_test_discovery_failed'
timeout --kill-after=5s 45s dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj \
  -c Release --no-build --nologo --list-tests --filter "$test_filter" \
  > "$root/private/discovery.log" 2>&1
TEST_NAMES="$test_names" ROOT="$root" python3 - <<'PY'
import os
from pathlib import Path
lines = (Path(os.environ['ROOT']) / 'private/discovery.log').read_text(encoding='utf-8-sig').splitlines()
expected = os.environ['TEST_NAMES'].splitlines()
for name in expected:
    assert sum(line.strip() == name for line in lines) == 1, 'Exact selected test discovery count is not one'
discovered = [line.strip() for line in lines if line.strip().startswith('AgentPortal.Tests.')]
assert sorted(discovered) == sorted(expected), 'Discovery selected unexpected tests'
PY

failure='observation_execution_failed'
export LEGEND_VALIDATION_TEST_PROCESS_STARTED=true
set +e
timeout --kill-after=5s "${stage_budget}s" dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj \
  -c Release --no-build --nologo --filter "$test_filter" \
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
ROOT="$root" STARTED="$started" TEST_NAMES="$test_names" OBSERVATION_EXIT="$observation_exit" python3 - <<'PY'
import datetime, json, os, re, xml.etree.ElementTree as ET
from pathlib import Path
root = Path(os.environ['ROOT'])
started = datetime.datetime.fromisoformat(os.environ['STARTED'].replace('Z', '+00:00'))
now = datetime.datetime.now(datetime.timezone.utc)
trx_path = root / 'private/observation.trx'
result_path = root / 'observation.json'
resource_scope = os.environ['LEGEND_VALIDATION_SCOPE'] == 'provider_resources'
expected_tests = os.environ['TEST_NAMES'].splitlines()
for path in ((trx_path,) if resource_scope else (trx_path, result_path)):
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
           {'total': len(expected_tests), 'executed': len(expected_tests), 'passed': len(expected_tests), 'failed': 0, 'notExecuted': 0}.items()), 'TRX did not record exactly the selected passing executions'
results = trx.findall('t:Results/t:UnitTestResult', ns)
assert len(results) == len(expected_tests) and all(item.attrib['outcome'] == 'Passed' for item in results), 'TRX results are not the selected passes'
definitions = trx.findall('t:TestDefinitions/t:UnitTest', ns)
assert len(definitions) == len(expected_tests), 'TRX definition count mismatch'
assert len({item.attrib['testId'] for item in results}) == len(results), 'TRX repeated a test identity'
assert len({item.attrib['executionId'] for item in results}) == len(results), 'TRX repeated an execution identity'
test_windows = {}
for item in results:
    matching = [entry for entry in definitions if entry.attrib['id'] == item.attrib['testId']]
    assert len(matching) == 1, 'TRX identity mismatch'
    execution = matching[0].find('t:Execution', ns)
    assert execution is not None and execution.attrib['id'] == item.attrib['executionId'], 'TRX execution identity mismatch'
    method = matching[0].find('t:TestMethod', ns)
    assert method is not None, 'TRX method identity missing'
    name = method.attrib['className'].split(',')[0] + '.' + method.attrib['name']
    assert name in expected_tests and name not in test_windows, 'TRX executed another or repeated test'
    test_start, test_finish = timestamp(item.attrib['startTime']), timestamp(item.attrib['endTime'])
    assert run_start <= test_start <= test_finish <= run_finish, 'TRX test timestamps do not belong to this run'
    test_windows[name] = (test_start, test_finish)
assert set(test_windows) == set(expected_tests), 'TRX test coverage mismatch'
if resource_scope:
    for resource, name in zip(('azure', 'research', 'openai'), expected_tests):
        path = root / ('resource-' + resource + '.json')
        assert path.is_file() and started.timestamp() <= path.stat().st_mtime <= now.timestamp(), 'Fresh resource receipt missing'
        receipt = json.loads(path.read_text(encoding='utf-8-sig'))
        assert receipt['CandidateSha'] == os.environ['LEGEND_VALIDATION_CANDIDATE_SHA']
        assert receipt['RunIdentity'] == os.environ['LEGEND_VALIDATION_RUN_IDENTITY']
        assert receipt['Authority'] == 'NonAuthoritativeResourceBoundaryDiagnostic'
        assert receipt['Environment'] == 'LocalInMemoryObservabilityWithLiveProvider'
        assert receipt['Resource'] == resource and receipt['Status'] == 'OBSERVED'
        test_start, test_finish = test_windows[name]
        assert test_start <= timestamp(receipt['StartedUtc']) <= timestamp(receipt['CompletedUtc']) <= test_finish
        assert receipt['CredentialConfigured'] is True and receipt['EndpointConfigured'] is True
        assert receipt['CanonicalWriteAttempts'] == 0 and receipt['LocalObservabilityWrites'] >= 0
        assert receipt['ElapsedMilliseconds'] >= 0 and receipt['HttpCallCount'] >= 1
        assert len(receipt['HttpCalls']) == min(64, receipt['HttpCallCount'])
        assert receipt['HttpCallsDropped'] == receipt['HttpCallCount'] - len(receipt['HttpCalls'])
        assert all(call['ElapsedMilliseconds'] >= 0 and (call['StatusCode'] is None or
            100 <= call['StatusCode'] <= 599) for call in receipt['HttpCalls'])
        clients = {'azure': {'AzureTranslator'}, 'research': {'LegendInternetResearchSearch', 'LegendResearchPageRetrieval'},
                   'openai': {'OpenAI'}}[resource]
        assert all(call['Client'] in clients for call in receipt['HttpCalls'])
        accepted_client = {'azure': 'AzureTranslator', 'research': 'LegendInternetResearchSearch', 'openai': 'OpenAI'}[resource]
        assert any(call['Client'] == accepted_client and call['StatusCode'] is not None and
                   200 <= call['StatusCode'] < 300 for call in receipt['HttpCalls']), 'Selected live provider has no accepted HTTP receipt'
        stages = receipt['Stages']
        assert isinstance(stages, list) and 0 < len(stages) <= 16
        assert all(isinstance(stage['Stage'], str) and isinstance(stage['Outcome'], str) and
                   stage['ElapsedMilliseconds'] >= 0 for stage in stages)
        def observed_stage(name, outcome):
            return any(stage['Stage'] == name and stage['Outcome'] == outcome for stage in stages)
        outcome = receipt['Outcome']
        assert outcome['Serving'] == 'NonServing' and outcome['Canonical'] == 'NonCanonical'
        if resource == 'azure':
            assert outcome['Policy'] == 'ProviderEnabled' and outcome['Provider'] == 'AzureTranslator'
            assert outcome['Succeeded'] is True and outcome['OutputPresent'] is True
            assert outcome['Provenance'] == 'ProviderDerived'
            assert outcome['NativeOnlyReason'] == 'external_provider_forbidden_by_native_only_policy'
            assert observed_stage('native_only_policy', 'UNAVAILABLE') and observed_stage('provider_translation', 'SUCCEEDED')
        elif resource == 'research':
            assert outcome['Policy'] == 'ProviderEnabled' and outcome['Outcome'] in ('Conclusion', 'InsufficientEvidence', 'UnresolvedConflict')
            assert outcome['IsReadOnly'] is True and outcome['ZeroWrite'] is True
            assert outcome['QueryReceipts'] > 0 and receipt['LocalObservabilityWrites'] > 0
            assert outcome['NativeOnlyReason'] == 'native_only_external_research_forbidden'
            assert observed_stage('native_only_policy', 'RESEARCH_NOT_AUTHORIZED')
            assert observed_stage('research_policy', 'RESEARCH_REQUIRED')
            assert observed_stage('native_only_execution', 'Failure')
            assert observed_stage('governed_research', outcome['Outcome'])
        else:
            assert receipt['SaveChangesAttempts'] == receipt['OperationsInvocationCount'] == 0
            assert receipt['ProviderClientCount'] >= 1
            assert outcome['Provider'] == 'OpenAI' and outcome['Policy'] == 'ExplicitZeroWriteCatalogCanary'
            assert outcome['ResponsePresent'] is True and outcome['ProviderStore'] is False
            assert outcome['ToolExecution'] == 'Disabled' and outcome['Provenance'] == 'ProviderDerived'
            assert observed_stage('candidate_assembly', 'VERIFIED') and observed_stage('provider_catalog_acceptance', 'OBSERVED')
    assert int(os.environ['OBSERVATION_EXIT']) == 0, 'Resource test process did not pass'
    raise SystemExit(0)
test_start, test_finish = test_windows[expected_tests[0]]
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
def validate_native_diagnostics(result, diagnostic_cases, extra_windows=()):
    assert result['DiagnosticsVersion'] == 'legend-runtime-diagnostic-v1'
    assert result['SqlFailureCount'] == 0 and type(result['DiagnosticsTruncated']) is bool
    sql_windows = {}
    any_truncated = False
    for evidence in [case['DiagnosticEvidence'] for case in diagnostic_cases] + list(extra_windows):
        assert isinstance(evidence, dict), 'Executed case lacks diagnostic evidence'
        stage, sql = evidence['StageEvents'], evidence['SqlCommands']
        assert isinstance(evidence['SqlSnapshotReference'], str) and evidence['SqlSnapshotReference']
        assert isinstance(evidence['EvidencePrerequisites'], dict)
        assert all(type(value) is int and value >= 0 for value in evidence['EvidencePrerequisites'].values())
        for snapshot, observed_key, limit in ((stage, 'ObservedEvents', 4096), (sql, 'EventsObserved', 256)):
            assert type(snapshot[observed_key]) is int and snapshot[observed_key] >= 0
            assert isinstance(snapshot['Records'], list) and len(snapshot['Records']) <= limit
            assert type(snapshot['RecordsDropped']) is int and snapshot['RecordsDropped'] >= 0
            assert snapshot[observed_key] == len(snapshot['Records']) + snapshot['RecordsDropped']
            assert snapshot['Truncated'] is (snapshot['RecordsDropped'] > 0)
            ordinals = [record['Ordinal'] for record in snapshot['Records']]
            assert all(type(value) is int and 1 <= value <= snapshot[observed_key] for value in ordinals)
            assert ordinals == sorted(set(ordinals)), 'Diagnostic event ordinals are invalid'
            any_truncated |= snapshot['Truncated']
        assert type(stage['ExceptionEvents']) is int and 0 <= stage['ExceptionEvents'] <= stage['ObservedEvents']
        assert stage['SqlFailureEvents'] == 0, 'A swallowed SQL log failure cannot pass'
        counts = [sql[key] for key in ('SucceededEvents', 'FailedEvents', 'CanceledEvents', 'BlockedEvents')]
        assert all(type(value) is int and value >= 0 for value in counts)
        assert sum(counts) == sql['EventsObserved']
        assert sql['FailedEvents'] == sql['CanceledEvents'] == sql['BlockedEvents'] == 0
        for command in sql['Records']:
            assert command['Outcome'] == 'succeeded' and command['ElapsedMilliseconds'] >= 0
            assert re.fullmatch('[0-9a-f]{64}', command['QueryFingerprint'])
            assert command['ExceptionType'] is None and command['HResult'] is None and command['SqlErrorNumber'] is None
        for event in stage['Records']:
            assert event['SqlErrorNumber'] is None, 'SQL error record contradicts successful diagnostics'
        prior = sql_windows.setdefault(evidence['SqlSnapshotReference'], sql)
        assert prior == sql, 'The same SQL snapshot reference contains conflicting evidence'
    assert sum(snapshot['FailedEvents'] for snapshot in sql_windows.values()) == result['SqlFailureCount']
    assert any_truncated is result['DiagnosticsTruncated'], 'Diagnostic truncation summary is inconsistent'
    for case in diagnostic_cases:
        diagnosis = case['FailureDiagnosis']
        assert all(isinstance(diagnosis[key], str) and diagnosis[key] for key in (
            'ObservedStage', 'AuthorityMethod', 'Classification', 'RootCauseStatus', 'NextVerification'))

if os.environ['LEGEND_VALIDATION_SCOPE'] == 'canonical_matrix':
    assert result['MatrixVersion'] == 'lai-027-029-v1' and result['IsolatedReadOnlyMode'] is True
    assert result['DiagnosticsVersion'] == 'legend-runtime-diagnostic-v1'
    assert result['ExercisedBoundaries'] == {'InProcessNativeSql': True, 'LiveProvider': False, 'AuthenticatedHttp': False}
    assert result['SqlFailureCount'] == 0 and result['NotExecutedCases'] == 0
    assert type(result['DiagnosticsTruncated']) is bool
    expected = {'exact_endpoint', 'held_out_paraphrase', 'discourse', 'cross_family_negative',
                'deduction', 'uncertainty', 'diagnosis', 'planning', 'audience_constraints',
                'language_routing', 'native_only_isolation'}
    assert set(result['Categories']) == expected
    assert {case['Category'] for case in cases} == expected
    assert result['ExecutedCases'] == result['TotalCases'] == len(cases)
    assert 11 <= len(cases) <= 16 and result['FailedCases'] == 0
    assert result['NativePasses'] >= 1 and result['NegativePasses'] >= 1
    assert result['NativePasses'] + result['NegativePasses'] == len(cases)
    assert result['ExternalTranslationProviderCallAttempts'] == 0
    references = [case['Reference'] for case in cases]
    assert all(isinstance(reference, str) and reference for reference in references)
    assert len(set(references)) == len(references), 'Canonical matrix case references are duplicated'
    for reference in ('automatic-language-governed-greeting', 'automatic-language-native-arithmetic'):
        automatic = [case for case in cases if case['Reference'] == reference]
        assert len(automatic) == 1 and automatic[0]['ExpectedNative'] is True, 'Required automatic-language positive coverage is missing'
        assert automatic[0]['UsesAutomaticLanguageIdentification'] is True
        assert automatic[0]['Category'] == 'language_routing'
    assert all(type(case['UsesAutomaticLanguageIdentification']) is bool for case in cases)
    validate_native_diagnostics(result, cases)
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
    assert isinstance(result['DiagnosticWindows'], list) and result['DiagnosticWindows']
    validate_native_diagnostics(result, cases, result['DiagnosticWindows'])
    window_references = [window['SqlSnapshotReference'] for window in result['DiagnosticWindows']]
    assert len(window_references) == len(set(window_references)), 'Observation diagnostic windows are duplicated'
    assert 'observation-preflight/sql' in window_references, 'Observation preflight diagnostic window is missing'
    assert {case['DiagnosticEvidence']['SqlSnapshotReference'] for case in cases} <= set(window_references)
    expected = {'learning', 'machine-learning-lifecycle', 'governed_cohort',
                'held-out-competing-hypotheses', 'held-out-discriminating-check', 'native-only-provider-isolation'}
    assert set(result['Coverage']) == expected
    assert result['ExecutedCases'] == len(cases) == len(expected)
    assert {case['Category'] for case in cases} == expected
    lifecycle = [case for case in cases if case['Category'] == 'machine-learning-lifecycle']
    assert len(lifecycle) == 1 and lifecycle[0]['LanguageCode'] == 'en'
    assert lifecycle[0]['MinimumRows'] == 1 and lifecycle[0]['Rows'] >= 1, 'English lifecycle evidence is empty or missing'
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
