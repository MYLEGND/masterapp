#!/usr/bin/env bash
set -euo pipefail

EXCLUDES=(
  --exclude-dir=bin
  --exclude-dir=obj
  --exclude-dir=node_modules
  --exclude-dir=.git
  --exclude-dir=lib
  --exclude-dir=dist
  --exclude-dir=wwwroot_backup
  --exclude='*.min.js'
)

echo "=== 1) INTERNAL / TEST / BOT CLASSIFICATION ==="
grep -Rni "${EXCLUDES[@]}" \
  "TrafficType.Internal\|TrafficType.Test\|TrafficType.BotSuspicious\|ToReportingBucket\|IsInternal\|FounderGuard\|localhost\|ExcludeLocalHosts\|User.Identity\|IsAuthenticated" \
  AgentPortal Protect-Website Infrastructure Domain Shared || true

echo
echo "=== 2) SOURCE ATTRIBUTION / NORMALIZATION ==="
grep -Rni "${EXCLUDES[@]}" \
  "SourceBucketLabel\|CampaignBucketLabel\|UtmSource\|utm_source\|fbclid\|gclid\|BuildSessionAttributionMap\|BuildVisitorAttributionMap\|ResolveAttribution\|HasAttributionSignal" \
  AgentPortal Protect-Website Infrastructure Domain Shared || true

echo
echo "=== 3) SUSPICION / TRUST SCORING SIGNALS ==="
grep -Rni "${EXCLUDES[@]}" \
  "page_exit\|scroll_depth\|page_engaged\|cta_click\|quote_click\|form_start\|form_abandon\|dead_click\|visibilitychange\|BotSuspicious\|MetaSignal\|ScoreTier\|score" \
  AgentPortal Protect-Website Infrastructure Domain Shared || true

echo
echo "=== 4) VISITOR TIMELINE / CONCENTRATION MODAL ==="
grep -Rni "${EXCLUDES[@]}" \
  "VisitorConcentration\|visitorConcentration\|visitor concentration\|ClientVisitorId\|VisitorId\|SessionId\|journey\|timeline\|kpi-detail\|modal" \
  AgentPortal/wwwroot AgentPortal/Views AgentPortal/Controllers AgentPortal/Services AgentPortal/Models || true

echo
echo "=== 5) DTOs / JS FILTERS THAT WILL NEED UI WIRING ==="
grep -Rni "${EXCLUDES[@]}" \
  "trafficType\|TrafficType\|IncludeInternal\|excludeInternal\|visitorConcentration\|kpi-detail\|loadKpiDetail\|modal" \
  AgentPortal/wwwroot/js AgentPortal/Views AgentPortal/Models AgentPortal/Controllers || true

echo
echo "DONE"
