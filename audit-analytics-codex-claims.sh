#!/usr/bin/env bash
set -euo pipefail

echo "=== 1) TrafficType mixing source + trust ==="
grep -Rni "enum TrafficType\|PaidAds\|Direct\|Referral\|Internal\|Test\|BotSuspicious" \
  Shared Domain Infrastructure AgentPortal Protect-Website 2>/dev/null || true

echo
echo "=== 2) BaseEvents/BaseLeads early internal suppression ==="
grep -Rni "IsInternal\|BaseEvents\|BaseLeads" AgentPortal/Services AgentPortal/Controllers Infrastructure 2>/dev/null || true

echo
echo "=== 3) Tracking client hard-coded IsInternal false ==="
grep -Rni "IsInternal.*false\|isInternal.*false\|FounderGuard\|IsFounder" \
  Protect-Website AgentPortal 2>/dev/null || true

echo
echo "=== 4) Attribution persistence / inheritance ==="
grep -Rni "utmSource\|utm_source\|fbclid\|gclid\|ig\|instagram\|facebook\|SourceBucketLabel\|ApplySession\|visitor" \
  Protect-Website/wwwroot/js AgentPortal/Services AgentPortal/Controllers 2>/dev/null || true

echo
echo "=== 5) SPA route-change handling ==="
grep -Rni "pushState\|replaceState\|popstate\|hashchange" \
  Protect-Website/wwwroot/js Protect-Website/Views 2>/dev/null || true

echo
echo "=== 6) form_start dedupe / repeated starts measurable? ==="
grep -Rni "form_start\|trackFormStart\|startedForms\|dedup\|Set()" \
  Protect-Website/wwwroot/js Protect-Website/Views 2>/dev/null || true

echo
echo "=== 7) Visitor concentration duplicate APIs/UI ==="
grep -Rni "VisitorConcentration\|visitor concentration\|visitorConcentration\|kpi-detail" \
  AgentPortal/Controllers AgentPortal/Services AgentPortal/wwwroot/js AgentPortal/Views 2>/dev/null || true

echo
echo "=== 8) Visitor timeline endpoint exists? ==="
grep -Rni "timeline\|journey\|session.*events\|visitor.*events\|ClientVisitorId\|VisitorId" \
  AgentPortal/Controllers AgentPortal/Services AgentPortal/Models AgentPortal/wwwroot/js 2>/dev/null || true

echo
echo "=== 9) Behavior / abandon context fields ==="
grep -Rni "lastFocusedField\|lastCompletedField\|submitAttempted\|consentInteracted\|completedFieldCount\|errorCount\|timeOnFormMs\|abandonReason" \
  AgentPortal Protect-Website 2>/dev/null || true

echo
echo "=== 10) DTOs that need trust/source split ==="
grep -Rni "TrafficType\|Source\|Medium\|Campaign\|IsInternal\|Suspicious\|Bot\|Trust" \
  AgentPortal/Models AgentPortal/DTOs AgentPortal/Services Domain Shared 2>/dev/null || true
