#!/usr/bin/env bash
set -euo pipefail

FILES="
AgentPortal/Services/Analytics/TrafficAttribution.cs
AgentPortal/Services/Analytics/AnalyticsQueryService.cs
AgentPortal/Services/Analytics/MetaSignalAnalyticsService.cs
AgentPortal/Controllers/API/AnalyticsIngestController.cs
AgentPortal/Controllers/WebsiteAnalyticsController.cs
AgentPortal/Controllers/VisitorConcentrationController.cs
AgentPortal/wwwroot/js/website-analytics.js
AgentPortal/wwwroot/js/website-analytics-kpi-modal.js
AgentPortal/Views/WebsiteAnalytics/Index.cshtml
Protect-Website/wwwroot/js/tracking.js
Protect-Website/Controllers/TrackingProxyController.cs
Domain/Entities/AnalyticsEvent.cs
"

echo "=== 1) INTERNAL / TEST / BOT CLASSIFICATION ==="
grep -n "TrafficType.Internal\|TrafficType.Test\|TrafficType.BotSuspicious\|ToReportingBucket\|IsInternal\|FounderGuard\|localhost\|ExcludeLocalHosts\|IsAuthenticated" $FILES || true

echo
echo "=== 2) SOURCE ATTRIBUTION / NORMALIZATION ==="
grep -n "SourceBucketLabel\|CampaignBucketLabel\|UtmSource\|utm_source\|fbclid\|gclid\|BuildSessionAttributionMap\|BuildVisitorAttributionMap\|ResolveAttribution\|HasAttributionSignal" $FILES || true

echo
echo "=== 3) SUSPICION / TRUST SCORING SIGNALS ==="
grep -n "page_exit\|scroll_depth\|page_engaged\|cta_click\|quote_click\|form_start\|form_abandon\|dead_click\|visibilitychange\|BotSuspicious\|ScoreTier\|score" $FILES || true

echo
echo "=== 4) VISITOR TIMELINE / CONCENTRATION MODAL ==="
grep -n "VisitorConcentration\|visitorConcentration\|ClientVisitorId\|VisitorId\|SessionId\|journey\|timeline\|kpi-detail\|modal" $FILES || true

echo
echo "DONE"
