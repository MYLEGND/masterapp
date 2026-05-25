#!/usr/bin/env bash
set -euo pipefail

echo "=============================="
echo "PRECISE ANALYTICS AUDIT"
echo "=============================="

EXCLUDES=(
  --exclude-dir=node_modules
  --exclude-dir=bin
  --exclude-dir=obj
  --exclude-dir=dist
  --exclude-dir=lib
  --exclude-dir=wwwroot_backup
  --exclude-dir=.git
  --exclude='*.min.js'
)

echo
echo "=== 1) TrafficType mixing trust + source ==="
grep -Rni "${EXCLUDES[@]}" \
  "enum TrafficType\|PaidAds\|NonPaid\|Internal\|Test\|BotSuspicious" \
  AgentPortal Shared Domain Infrastructure Protect-Website || true

echo
echo "=== 2) Internal suppression before reporting layer ==="
grep -Rni "${EXCLUDES[@]}" \
  "IsInternal\|BaseEvents\|BaseLeads" \
  AgentPortal/Services AgentPortal/Controllers Infrastructure || true

echo
echo "=== 3) Hard-coded IsInternal false ==="
grep -Rni "${EXCLUDES[@]}" \
  "IsInternal.*false\|isInternal.*false" \
  Protect-Website AgentPortal || true

echo
echo "=== 4) Founder/admin detection ==="
grep -Rni "${EXCLUDES[@]}" \
  "FounderGuard\|IsFounder\|Admin\|Authenticated" \
  AgentPortal Protect-Website || true

echo
echo "=== 5) Attribution persistence ==="
grep -Rni "${EXCLUDES[@]}" \
  "utmSource\|utm_source\|fbclid\|gclid\|campaign\|medium\|visitor attribution" \
  Protect-Website/wwwroot/js AgentPortal/Services AgentPortal/Controllers || true

echo
echo "=== 6) Source normalization ==="
grep -Rni "${EXCLUDES[@]}" \
  "SourceBucketLabel\|instagram\|facebook\|ig\|fb\|meta" \
  AgentPortal/Services || true

echo
echo "=== 7) SPA navigation tracking ==="
grep -Rni "${EXCLUDES[@]}" \
  "pushState\|replaceState\|popstate\|hashchange" \
  Protect-Website/wwwroot/js Protect-Website/Views || true

echo
echo "=== 8) form_start dedupe ==="
grep -Rni "${EXCLUDES[@]}" \
  "form_start\|trackFormStart\|startedForms\|dedup" \
  Protect-Website/wwwroot/js Protect-Website/Views || true

echo
echo "=== 9) Visitor concentration duplicate systems ==="
grep -Rni "${EXCLUDES[@]}" \
  "VisitorConcentration\|visitor concentration\|visitorConcentration" \
  AgentPortal || true

echo
echo "=== 10) Visitor timeline support ==="
grep -Rni "${EXCLUDES[@]}" \
  "ClientVisitorId\|VisitorId\|timeline\|journey\|session events" \
  AgentPortal || true

echo
echo "=== 11) Abandonment metadata ==="
grep -Rni "${EXCLUDES[@]}" \
  "lastFocusedField\|lastCompletedField\|submitAttempted\|consentInteracted\|completedFieldCount\|errorCount\|timeOnFormMs\|abandonReason" \
  AgentPortal Protect-Website || true

echo
echo "=============================="
echo "AUDIT COMPLETE"
echo "=============================="
