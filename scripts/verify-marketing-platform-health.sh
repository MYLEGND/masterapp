#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SECONDS=0
CURRENT_STAGE="initializing"

on_error() {
  local exit_code=$?
  local elapsed="$SECONDS"
  echo "[marketing-health] stage failed: ${CURRENT_STAGE}"
  echo "[marketing-health] overall result: FAIL (${elapsed}s elapsed)"
  exit "$exit_code"
}

trap on_error ERR

run_stage() {
  local stage_name="$1"
  shift
  local stage_started="$SECONDS"
  CURRENT_STAGE="$stage_name"
  echo "[marketing-health] >>> ${stage_name}"
  "$@"
  local stage_elapsed=$((SECONDS - stage_started))
  echo "[marketing-health] <<< ${stage_name} PASS (${stage_elapsed}s)"
}

TEST_PROJECT="AgentPortal.Tests/AgentPortal.Tests.csproj"
TEST_LOGGER="console;verbosity=minimal"

run_stage "build verification: shared" \
  dotnet build SHARED/Shared.csproj --nologo

run_stage "build verification: protect website" \
  dotnet build Protect-Website/ProtectWebsite.csproj --nologo

run_stage "build verification: agent portal" \
  dotnet build AgentPortal/AgentPortal.csproj --nologo

run_stage "build verification: tests" \
  dotnet build "$TEST_PROJECT" --nologo

run_stage "shared catalog tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~AnalyticsEventCatalogTests"

run_stage "ingest tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~AnalyticsIngestControllerTests"

run_stage "quote funnel tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~AnalyticsQueryServiceQuoteFunnelTests|FullyQualifiedName~AnalyticsQueryServiceMarketingHealthTests|FullyQualifiedName~QuoteProductInstrumentationContractTests|FullyQualifiedName~ThankYouMetaFallbackContractTests|FullyQualifiedName~TrackingFailLoudContractTests|FullyQualifiedName~WebsiteLifeLeadCaptureServiceTests"

run_stage "attribution tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~TrafficAttributionTests|FullyQualifiedName~LeadSnapshotAttributionTests"

run_stage "meta signal tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~MetaSignalContractTests"

run_stage "ai review contract tests" \
  dotnet test "$TEST_PROJECT" --no-build --nologo --logger:"$TEST_LOGGER" \
    --filter "FullyQualifiedName~WebsiteAnalyticsAiContractTests|FullyQualifiedName~WebsiteAnalyticsAiRedactorTests"

echo "[marketing-health] overall result: PASS (${SECONDS}s elapsed)"
