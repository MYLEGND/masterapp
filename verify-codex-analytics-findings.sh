#!/usr/bin/env bash
set -euo pipefail

echo "=== 1) Catalog-approved event filtering ==="
grep -n "allowedEvents\|EventCatalog\|catalog\|sendEvent\|return false\|not allowed" Protect-Website/wwwroot/js/tracking.js | head -120

echo
echo "=== 2) Health uncataloged events ==="
grep -n "health_quote_processing_view\|health_quote_started\|quote_thank_you_view\|form_start\|data-form-key" Protect-Website/Views/Quote/Health.cshtml | head -160

echo
echo "=== 3) Disability uncataloged events ==="
grep -n "disability_quote_processing_view\|disability_quote_started\|quote_thank_you_view\|form_start\|data-form-key" Protect-Website/Views/Quote/Disability.cshtml | head -160

echo
echo "=== 4) Shared tracking catalog names ==="
grep -Rni --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
"health_quote_processing_view\|health_quote_started\|disability_quote_processing_view\|disability_quote_started\|quote_thank_you_view" \
Shared Domain Infrastructure AgentPortal Protect-Website | head -200

echo
echo "=== 5) Marketing Health client tracking error counting ==="
grep -n "client_tracking_error\|window_error\|fetch_failed\|fetch_non_ok\|resource\|Client Tracking Errors\|TrackingError" AgentPortal/Services/Analytics/AnalyticsQueryService.cs Protect-Website/wwwroot/js/tracking.js | head -220

echo
echo "=== 6) page_exit send path + behavior dependency ==="
grep -n "page_exit\|navigator.sendBeacon\|keepalive\|visibilitychange\|DwellMilliseconds\|Quick Exit\|Exit" Protect-Website/wwwroot/js/tracking.js AgentPortal/Services/Analytics/AnalyticsQueryService.cs | head -220

echo
echo "=== 7) Device Intelligence grouping math ==="
sed -n '3240,3335p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs

echo
echo "=== 8) Session Activity timezone formatting ==="
grep -n "formatActivityTimeRange\|formatDisplayDate\|new Date\|UTC" AgentPortal/wwwroot/js/website-analytics.js | head -160

echo
echo "=== 9) Attribution earliest signal bias ==="
sed -n '690,770p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs

echo
echo "=== 10) Internal handling separation ==="
grep -n "IsInternal\|BaseEvents\|BaseLeads" AgentPortal/Services/Analytics/AnalyticsQueryService.cs Protect-Website/wwwroot/js/tracking.js AgentPortal/Services/Analytics/TrafficAttribution.cs | head -220

echo
echo "=== 11) Founder fallback / default domain profile ==="
grep -n "default-domain\|default domain\|Founder\|fallback\|resolved slug\|slug\|AgentTrackingProfile" Protect-Website/Controllers/TrackingProxyController.cs | head -220

echo
echo "DONE"
