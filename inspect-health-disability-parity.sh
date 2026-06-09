#!/usr/bin/env bash
set -euo pipefail

echo "=== 1) Health submit attempt/failure/success tracking ==="
sed -n '3125,3410p' Protect-Website/Views/Quote/Health.cshtml

echo
echo "=== 2) Disability submit attempt/failure/success tracking ==="
sed -n '2935,3220p' Protect-Website/Views/Quote/Disability.cshtml

echo
echo "=== 3) Life canonical guarded start/submit pattern ==="
sed -n '10020,10210p' Protect-Website/Views/Quote/Life.cshtml

echo
echo "=== 4) tracking.js form_start guard and allowed catalog behavior ==="
sed -n '1280,1325p' Protect-Website/wwwroot/js/tracking.js
sed -n '610,635p' Protect-Website/wwwroot/js/tracking.js

echo
echo "=== 5) Remaining Health/Disability custom submit events ==="
grep -Rni "health_.*submit\|disability_.*submit\|submit_attempt\|submit_failure\|submit_success\|lead_form_submit_success\|form_submit" \
Protect-Website/Views/Quote/Health.cshtml Protect-Website/Views/Quote/Disability.cshtml Protect-Website/Views/Quote/Life.cshtml | head -240

echo
echo "=== 6) Device Intelligence submit/lead counting block ==="
sed -n '3300,3385p' AgentPortal/Services/Analytics/AnalyticsQueryService.cs

echo
echo "=== 7) Build check ==="
dotnet build Protect-Website/ProtectWebsite.csproj
dotnet build AgentPortal/AgentPortal.csproj

echo "DONE"
