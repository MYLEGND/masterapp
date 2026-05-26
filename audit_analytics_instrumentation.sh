#!/usr/bin/env bash
set -euo pipefail

echo "=== 1) FIND ANALYTICS ENTITY / DTO / INGEST FILES ==="
grep -RIn --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
"AnalyticsEvent\|AnalyticsIngest\|UserAgent\|IpAddress\|RemoteIpAddress\|X-Forwarded-For\|DeviceType\|Browser\|OperatingSystem\|TimeZone\|Language" \
AgentPortal Protect-Website Domain Infrastructure Shared 2>/dev/null || true

echo ""
echo "=== 2) FIND TRACKING JS CLIENT CONTEXT ==="
grep -RIn --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
"navigator.userAgent\|userAgentData\|webdriver\|screen.width\|viewport\|DeviceType\|Browser\|OperatingSystem\|TimeZone\|Language\|sendBeacon\|fetch" \
Protect-Website/wwwroot AgentPortal/wwwroot 2>/dev/null || true

echo ""
echo "=== 3) FIND ANALYTICS EVENT EF CONFIG ==="
grep -RIn --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
"AnalyticsEvent\|HasMaxLength\|Property(x => x.Browser\|Property(x => x.OperatingSystem\|Property(x => x.DeviceType\|TimeZone\|Language" \
Infrastructure AgentPortal Domain 2>/dev/null || true

echo ""
echo "=== 4) FIND MIGRATIONS TOUCHING ANALYTICS EVENTS ==="
find . -path "*/Migrations/*.cs" -type f -print0 2>/dev/null | xargs -0 grep -In \
"AnalyticsEvents\|Browser\|OperatingSystem\|DeviceType\|TimeZone\|Language\|UserAgent\|IpAddress" 2>/dev/null || true

echo ""
echo "=== 5) SHOW LIKELY FILES ==="
ls -la Protect-Website/wwwroot/js 2>/dev/null || true
ls -la Protect-Website/Controllers 2>/dev/null || true
ls -la AgentPortal/Controllers/API 2>/dev/null || true
ls -la Domain/Entities 2>/dev/null || true
ls -la Infrastructure/Data 2>/dev/null || true
