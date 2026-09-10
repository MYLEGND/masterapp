#!/usr/bin/env bash
set -Eeuo pipefail

readonly root="${LEGEND_VALIDATION_ROOT:-diagnostics/legend-shadow}"
readonly stage0_budget=60
readonly stage1_budget=180
readonly stage2_budget=720
readonly artifact_budget=240
readonly overall_budget=1200
if [[ -n "${LEGEND_VALIDATION_STARTED_EPOCH_SECONDS:-}" ]]; then
  readonly started_ns="$((LEGEND_VALIDATION_STARTED_EPOCH_SECONDS * 1000000000))"
else
  readonly started_ns="$(date +%s%N)"
fi
mkdir -p "$root"

stage_name='initialization'
stage_started_ns="$started_ns"
status='failed'
failure='validation_not_completed'

milliseconds_since() {
  local origin_ns="$1"
  local now_ns
  now_ns="$(date +%s%N)"
  echo $(( (now_ns - origin_ns) / 1000000 ))
}

write_summary() {
  local total_ms stage_ms
  total_ms="$(milliseconds_since "$started_ns")"
  stage_ms="$(milliseconds_since "$stage_started_ns")"
  STATUS="$status" FAILURE="$failure" STAGE_NAME="$stage_name" \
  TOTAL_MS="$total_ms" STAGE_MS="$stage_ms" python3 - <<'PY'
import json, os
from pathlib import Path

root = Path(os.environ.get('LEGEND_VALIDATION_ROOT', 'diagnostics/legend-shadow'))
stage2 = {}
try:
    stage2 = json.loads((root / 'stage2.json').read_text(encoding='utf-8'))
except Exception:
    pass
summary = {
    'status': os.environ['STATUS'],
    'failure': os.environ['FAILURE'],
    'failedStage': os.environ['STAGE_NAME'],
    'candidateSha': os.environ.get('LEGEND_VALIDATION_CANDIDATE_SHA', ''),
    'matrixVersion': os.environ.get('LEGEND_PRODUCTION_PROOF_MATRIX_VERSION', ''),
    'totalWallMilliseconds': int(os.environ['TOTAL_MS']),
    'failureStageMilliseconds': int(os.environ['STAGE_MS']),
    'providerClientCount': int(stage2.get('ProviderClientCount', -1)),
    'providerHttpCallCount': int(stage2.get('ProviderHttpCallCount', -1)),
    'productionWriteCommandCount': int(stage2.get('ProductionWriteCommandCount', -1)),
    'skippedCases': int(stage2.get('SkippedCases', -1)),
}
(root / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
PY
}

cleanup() {
  write_summary || true
  jobs -pr | xargs -r kill 2>/dev/null || true
}
trap cleanup EXIT

require_lock() {
  local name="$1"
  test "${!name:-}" = 'true' || {
    failure="side_effect_lock_missing:$name"
    return 1
  }
}

run_test_stage() {
  local name="$1" budget="$2" filter="$3" log="$4"
  stage_name="$name"
  stage_started_ns="$(date +%s%N)"
  set +e
  timeout --foreground --signal=TERM "${budget}s" \
    dotnet test AgentPortal.Tests/AgentPortal.Tests.csproj \
      -c Release --no-build --nologo \
      --filter "FullyQualifiedName=$filter" \
      --logger 'console;verbosity=minimal' >"$log" 2>&1
  local exit_code=$?
  set -e
  local elapsed_ms
  elapsed_ms="$(milliseconds_since "$stage_started_ns")"
  if (( exit_code == 124 || exit_code == 143 )); then
    failure="${name}_timeout"
    return 1
  fi
  if (( exit_code != 0 )); then
    failure="${name}_failed_exit_${exit_code}"
    return 1
  fi
  if (( elapsed_ms > budget * 1000 )); then
    failure="${name}_budget_exceeded"
    return 1
  fi
}

stage_name='stage0'
stage_started_ns="$(date +%s%N)"
actual_sha="$(git rev-parse HEAD)"
test "$actual_sha" = "${LEGEND_VALIDATION_CANDIDATE_SHA:-}" || {
  failure='candidate_sha_mismatch'
  exit 1
}
test -n "${LEGEND_PRODUCTION_READONLY_CONNECTION:-}" || {
  failure='select_only_sql_credential_missing'
  exit 1
}
test -n "${LEGEND_PRODUCTION_READONLY_FOUNDER_OID:-}" || {
  failure='founder_identity_missing'
  exit 1
}
test -z "${OPENAI_API_KEY:-}" && test -z "${OpenAI__ApiKey:-}" || {
  failure='provider_credential_present'
  exit 1
}
require_lock LEGEND_DISABLE_PROVIDER_TRANSPORTS
require_lock LEGEND_DISABLE_QUEUES
require_lock LEGEND_DISABLE_NOTIFICATIONS
require_lock LEGEND_DISABLE_EXTERNAL_SIDE_EFFECTS
run_test_stage stage0 "$stage0_budget" \
  AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCredentialHasNoMutationAuthority \
  "$root/stage0.log"

run_test_stage stage1 "$stage1_budget" \
  AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyLiveCohortSmoke \
  "$root/stage1.log"

run_test_stage stage2 "$stage2_budget" \
  AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyNativeProofMatrix \
  "$root/stage2.log"

stage_name='artifacts'
stage_started_ns="$(date +%s%N)"
failure='artifacts_validation_failed'
timeout --foreground --signal=TERM "${artifact_budget}s" python3 - <<'PY'
import json, os, statistics
from pathlib import Path

root = Path(os.environ.get('LEGEND_VALIDATION_ROOT', 'diagnostics/legend-shadow'))
required = {
  'held_out_entity', 'held_out_relation', 'held_out_domain', 'held_out_phrasing',
  'first_ordinal', 'second_ordinal', 'last_ordinal', 'correction_recency',
  'genuine_ambiguity', 'missing_antecedent', 'cross_family_isolation',
  'cross_actor_isolation', 'persistence_reload', 'concurrency', 'authorization',
  'failure', 'rollback', 'adversarial', 'web_authority', 'ios_authority',
  'android_authority', 'original_articulation', 'claim_bound_citations',
  'native_only_isolation'
}
path = root / 'stage2.json'
if not path.exists():
    raise SystemExit('Stage 2 result is missing')
result = json.loads(path.read_text(encoding='utf-8'))
categories = set(result.get('Categories') or [])
missing = sorted(required - categories)
cases = result.get('CaseResults') or []
times = sorted(float(case.get('ElapsedMilliseconds') or 0) for case in cases)
if missing:
    raise SystemExit('Stage 2 coverage is incomplete: ' + ','.join(missing))
if result.get('Status') != 'passed' or int(result.get('FailedCases', -1)) != 0:
    raise SystemExit('Stage 2 did not pass')
if int(result.get('SkippedCases', 0)) != 0:
    raise SystemExit('Stage 2 contains skipped cases')
if int(result.get('ProviderClientCount', -1)) != 0 or int(result.get('ProviderHttpCallCount', -1)) != 0:
    raise SystemExit('Stage 2 observed provider activity')
if int(result.get('ProductionWriteCommandCount', -1)) != 0:
    raise SystemExit('Stage 2 observed a production write command')
required_metrics = {
  'QueryBudgetMilliseconds', 'CaseBudgetMilliseconds', 'ConnectionCount',
  'RowCounts', 'SlowestQueries', 'StageDurations'
}
missing_metrics = sorted(required_metrics - set(result))
if missing_metrics:
    raise SystemExit('Stage 2 performance evidence is incomplete: ' + ','.join(missing_metrics))
metrics = {
  'caseCount': len(times),
  'p50Milliseconds': statistics.median(times) if times else 0,
  'p95Milliseconds': times[max(0, min(len(times)-1, int(len(times)*0.95)-1))] if times else 0,
  'maximumCaseMilliseconds': max(times, default=0),
}
(root / 'performance.json').write_text(json.dumps(metrics, indent=2), encoding='utf-8')
PY

if (( $(milliseconds_since "$stage_started_ns") > artifact_budget * 1000 )); then
  failure='artifacts_budget_exceeded'
  exit 1
fi
if (( $(milliseconds_since "$started_ns") > overall_budget * 1000 )); then
  failure='overall_budget_exceeded'
  exit 1
fi

status='passed'
failure='none'
