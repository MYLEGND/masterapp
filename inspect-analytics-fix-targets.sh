#!/usr/bin/env bash
set -euo pipefail

echo "=================================================="
echo "1) EVENT CATALOG / ALLOWED BROWSER EVENTS"
echo "=================================================="
grep -Rni --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
"allowedBrowserEvents\|AnalyticsEventCatalog\|BrowserEvents\|clientTrackingErrorEvent\|quote_thank_you_view\|health_quote_started\|disability_quote_started" \
Protect-Website AgentPortal Shared Domain Infrastructure | head -220

echo
echo "=================================================="
echo "2) HEALTH CUSTOM TRACKING BLOCKS"
echo "=================================================="
sed -n '2928,3060p' Protect-Website/Views/Quote/Health.cshtml

echo
echo "=================================================="
echo "3) DISABILITY CUSTOM TRACKING BLOCKS"
echo "=================================================="
sed -n '2730,2860p' Protect-Website/Views/Quote/Disability.cshtml

echo
echo "=================================================="
echo "4) TRACKING.JS SEND EVENT + ERROR REPORTING"
echo "=================================================="
sed -n '580,710p' Protect-Website/wwwroot/js/tracking.js

echo
echo "=================================================="
echo "5) TRACKING.JS GLOBAL ERROR / FETCH ERROR"
echo "=================================================="
sed -n '1085,1170p' Protect-Website/wwwroot/js/tracking.js

echo
echo "=================================================="
echo "6) TRACKING.JS PAGE EXIT"
echo "=================================================="
sed -n '1010,1050p' Protect-Website/wwwroot/js/tracking.js
sed -n '520,545p' Protect-Website/wwwroot/js/tracking.js
sed -n '730,745p' Protect-Website/wwwroot/js/tracking.js

echo
echo "=================================================="
echo "7) DEVICE INTELLIGENCE METHOD"
echo "=================================================="
sed -n '3210,3335p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs

echo
echo "=================================================="
echo "8) SESSION ACTIVITY TIME FORMATTERS"
echo "=================================================="
sed -n '2475,2545p' AgentPortal/wwwroot/js/website-analytics.js

echo
echo "=================================================="
echo "9) ATTRIBUTION SELECTION METHODS"
echo "=================================================="
sed -n '690,770p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs

echo
echo "=================================================="
echo "10) BASE EVENTS / BASE LEADS / INTERNAL FILTERING"
echo "=================================================="
sed -n '36,78p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs
sed -n '485,510p' Protect-Website/wwwroot/js/tracking.js
sed -n '145,175p' AgentPortal/Services/Analytics/TrafficAttribution.cs

echo
echo "=================================================="
echo "11) FOUNDER DEFAULT-DOMAIN FALLBACK"
echo "=================================================="
sed -n '350,420p' Protect-Website/Controllers/TrackingProxyController.cs

echo
echo "=================================================="
echo "12) CURRENT BUILD STATUS"
echo "=================================================="
dotnet build AgentPortal/AgentPortal.csproj
dotnet build Protect-Website/ProtectWebsite.csproj

echo
echo "DONE - INSPECTION ONLY"
