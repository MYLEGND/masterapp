#!/usr/bin/env bash
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SECONDS=0
CURRENT_STAGE="initializing"
OVERALL_RESULT="FAIL"
LOG_DIR="$(mktemp -d "${TMPDIR:-/tmp}/marketing-health.XXXXXX")"
STAGE_COUNTER=0

BUILD_TIMEOUT_SECONDS=120
TEST_TIMEOUT_SECONDS=180

finish() {
  local exit_code=$?

  if [[ -d "$LOG_DIR" ]]; then
    rm -rf "$LOG_DIR"
  fi

  if (( exit_code == 0 )) && [[ "$OVERALL_RESULT" == "PASS" ]]; then
    echo "[marketing-health] overall result: PASS (${SECONDS}s elapsed)"
  else
    echo "[marketing-health] stage failed: ${CURRENT_STAGE}"
    echo "[marketing-health] overall result: FAIL (${SECONDS}s elapsed)"
  fi
}

trap finish EXIT

kill_process_group() {
  local signal="$1"
  local pid="$2"

  kill "-${signal}" -- "-${pid}" 2>/dev/null || kill "-${signal}" "$pid" 2>/dev/null || true
}

print_stage_command() {
  printf '[marketing-health] command:'
  printf ' %q' "$@"
  printf '\n'
}

run_stage() {
  local stage_name="$1"
  local timeout_seconds="$2"
  shift 2

  local stage_started="$SECONDS"
  local stage_log="${LOG_DIR}/stage-$(printf '%02d' "$STAGE_COUNTER").log"
  local stage_status=0
  local timed_out=0

  STAGE_COUNTER=$((STAGE_COUNTER + 1))
  CURRENT_STAGE="$stage_name"

  echo "[marketing-health] >>> ${stage_name}"
  print_stage_command "$@"

  perl -e '
    use strict;
    use warnings;
    use POSIX qw(setpgid);

    setpgid(0, 0) or die "setpgid failed: $!";
    exec @ARGV or die "exec failed: $!";
  ' "$@" >"$stage_log" 2>&1 &
  local stage_pid=$!

  while kill -0 "$stage_pid" 2>/dev/null; do
    local stage_elapsed=$((SECONDS - stage_started))

    if (( stage_elapsed >= timeout_seconds )); then
      timed_out=1
      echo "[marketing-health] !!! ${stage_name} TIMEOUT (${stage_elapsed}s)"
      kill_process_group TERM "$stage_pid"
      sleep 2

      if kill -0 "$stage_pid" 2>/dev/null; then
        kill_process_group KILL "$stage_pid"
      fi

      break
    fi

    sleep 1
  done

  if wait "$stage_pid"; then
    stage_status=0
  else
    stage_status=$?
  fi

  if [[ -s "$stage_log" ]]; then
    cat "$stage_log"
  fi

  local stage_elapsed=$((SECONDS - stage_started))

  if (( timed_out )); then
    echo "[marketing-health] <<< ${stage_name} FAIL (${stage_elapsed}s, timeout)"
    return 124
  fi

  if (( stage_status != 0 )); then
    echo "[marketing-health] <<< ${stage_name} FAIL (${stage_elapsed}s, exit ${stage_status})"
    return "$stage_status"
  fi

  echo "[marketing-health] <<< ${stage_name} PASS (${stage_elapsed}s)"
}

TEST_PROJECT="AgentPortal.Tests/AgentPortal.Tests.csproj"
BUILD_ARGS=(--nologo -v minimal --disable-build-servers --no-restore)
TEST_ARGS=(--no-build --no-restore --nologo -v minimal '--logger:console;verbosity=minimal' --disable-build-servers)

main() {
  run_stage "build verification: shared" "$BUILD_TIMEOUT_SECONDS" \
    dotnet build SHARED/Shared.csproj "${BUILD_ARGS[@]}" || return $?

  run_stage "build verification: protect website" "$BUILD_TIMEOUT_SECONDS" \
    dotnet build Protect-Website/ProtectWebsite.csproj "${BUILD_ARGS[@]}" || return $?

  run_stage "build verification: agent portal" "$BUILD_TIMEOUT_SECONDS" \
    dotnet build AgentPortal/AgentPortal.csproj "${BUILD_ARGS[@]}" || return $?

  run_stage "build verification: tests" "$BUILD_TIMEOUT_SECONDS" \
    dotnet build "$TEST_PROJECT" "${BUILD_ARGS[@]}" || return $?

  run_stage "shared catalog tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~AnalyticsEventCatalogTests' || return $?

  run_stage "ingest tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~AnalyticsIngestControllerTests' || return $?

  run_stage "quote funnel tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~AnalyticsQueryServiceQuoteFunnelTests|FullyQualifiedName~AnalyticsQueryServiceMarketingHealthTests|FullyQualifiedName~QuoteProductInstrumentationContractTests|FullyQualifiedName~ThankYouMetaFallbackContractTests|FullyQualifiedName~TrackingFailLoudContractTests|FullyQualifiedName~WebsiteLifeLeadCaptureServiceTests' || return $?

  run_stage "attribution tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~TrafficAttributionTests|FullyQualifiedName~LeadSnapshotAttributionTests' || return $?

  run_stage "meta signal tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~MetaSignalContractTests' || return $?

  run_stage "ai review contract tests" "$TEST_TIMEOUT_SECONDS" \
    dotnet test "$TEST_PROJECT" "${TEST_ARGS[@]}" \
    --filter 'FullyQualifiedName~WebsiteAnalyticsAiContractTests|FullyQualifiedName~WebsiteAnalyticsAiRedactorTests' || return $?

  OVERALL_RESULT="PASS"
}

main
