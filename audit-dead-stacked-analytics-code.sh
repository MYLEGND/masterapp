#!/usr/bin/env bash
set -euo pipefail

echo "=== 1) Duplicate helper definitions ==="
grep -Rni "function detectInternalTrafficFlag\|function shouldSkipFetchDiagnostics\|function parseAnalyticsDate\|AttributionStrength\|SelectStrongestAttribution\|ResolveSessionLabel\|openVisitorTimelineModal" \
AgentPortal Protect-Website | sort

echo
echo "=== 2) Removed dead Health/Disability event names ==="
grep -Rni "health_quote_processing_view\|health_quote_started\|health_quote_thank_you_view\|disability_quote_processing_view\|disability_quote_started\|disability_quote_thank_you_view\|quote_thank_you_view" \
Protect-Website AgentPortal Shared Domain Infrastructure || true

echo
echo "=== 3) Risky old raw attribution selectors ==="
grep -Rni "OrderBy(e => e.EventUtc).*FirstOrDefault(HasAttributionSignal)\|return attribution.UtmSource!.Trim()" \
AgentPortal/Services/Analytics/AnalyticsQueryService.cs || true

echo
echo "=== 4) Bad JS corruption patterns ==="
grep -Rni "\`)join\|setTimeout(() =>.*data-visitor-id\|IsInternal: false" \
AgentPortal/wwwroot/js Protect-Website/wwwroot/js || true

echo
echo "=== 5) Visitor row click attributes ==="
grep -n "data-visitor-id\|vc-clickable-row\|Click any visitor row" AgentPortal/wwwroot/js/website-analytics-kpi-modal.js

echo
echo "=== 6) Build check ==="
dotnet build AgentPortal/AgentPortal.csproj
dotnet build Protect-Website/ProtectWebsite.csproj

echo
echo "DONE"
