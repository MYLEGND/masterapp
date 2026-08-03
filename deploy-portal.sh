#!/usr/bin/env bash
set -euo pipefail

# ── AgentPortal → Azure Web App deployment ──────────────────────────────────
SUBSCRIPTION="LEGEND"
RESOURCE_GROUP="MASTERAPP-RG"
APP_NAME="masterapp-portal"
PROJECT="/Users/zacowen/MASTERAPP/AgentPortal/AgentPortal.csproj"
PUBLISH_DIR="/tmp/masterapp-portal-publish"
ZIP_PATH="/tmp/masterapp-portal.zip"

# The Azure target is Windows App Service, so Linux package-manager commands
# cannot provision FFmpeg on the host. Package this pinned LGPL static build
# with every release instead. The SHA-256 is verified before it enters the ZIP.
FFMPEG_RELEASE_TAG="autobuild-2026-07-31-14-10"
FFMPEG_ARCHIVE_NAME="ffmpeg-N-125875-g5d4d3bdc61-win64-lgpl.zip"
FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_RELEASE_TAG}/${FFMPEG_ARCHIVE_NAME}"
FFMPEG_SHA256="5d65df0c0ca5346d82df8ade9c2e12db45d1f978f18ff908b42f03f5223dfc90"
FFMPEG_ARCHIVE="/tmp/${FFMPEG_ARCHIVE_NAME}"
FFMPEG_EXTRACT_DIR="/tmp/masterapp-ffmpeg-win64-lgpl"
FFMPEG_SOURCE="${FFMPEG_EXTRACT_DIR}/ffmpeg-N-125875-g5d4d3bdc61-win64-lgpl/bin/ffmpeg.exe"
FFPROBE_SOURCE="${FFMPEG_EXTRACT_DIR}/ffmpeg-N-125875-g5d4d3bdc61-win64-lgpl/bin/ffprobe.exe"
FFMPEG_PUBLISH_DIR="${PUBLISH_DIR}/tools/ffmpeg"

echo "▶ Setting subscription..."
az account set --subscription "$SUBSCRIPTION"

echo "▶ Cleaning previous build..."
rm -rf "$PUBLISH_DIR" "$ZIP_PATH"

echo "▶ Publishing..."
dotnet publish "$PROJECT" -c Release -o "$PUBLISH_DIR"

echo "▶ Adding verified FFmpeg runtime..."
rm -rf "$FFMPEG_EXTRACT_DIR"
if ! printf "%s  %s\n" "$FFMPEG_SHA256" "$FFMPEG_ARCHIVE" | shasum -a 256 -c - >/dev/null 2>&1; then
  curl --fail --location --retry 3 --retry-delay 2 --continue-at - "$FFMPEG_URL" -o "$FFMPEG_ARCHIVE"
fi
printf "%s  %s\n" "$FFMPEG_SHA256" "$FFMPEG_ARCHIVE" | shasum -a 256 -c -
unzip -tq "$FFMPEG_ARCHIVE"
unzip -q "$FFMPEG_ARCHIVE" -d "$FFMPEG_EXTRACT_DIR"
test -s "$FFMPEG_SOURCE"
test -s "$FFPROBE_SOURCE"
mkdir -p "$FFMPEG_PUBLISH_DIR"
cp "$FFMPEG_SOURCE" "$FFMPEG_PUBLISH_DIR/ffmpeg.exe"
cp "$FFPROBE_SOURCE" "$FFMPEG_PUBLISH_DIR/ffprobe.exe"
test -s "$FFMPEG_PUBLISH_DIR/ffmpeg.exe"
test -s "$FFMPEG_PUBLISH_DIR/ffprobe.exe"

echo "▶ Applying production database migrations..."
PORTAL_CONNECTION_STRING=$(az webapp config connection-string list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --subscription "$SUBSCRIPTION" \
  --query "[?name=='MasterAppDb'].value | [0]" -o tsv)
if [ -z "$PORTAL_CONNECTION_STRING" ]; then
  PORTAL_CONNECTION_STRING=$(az webapp config appsettings list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$APP_NAME" \
    --subscription "$SUBSCRIPTION" \
    --query "[?name=='ConnectionStrings__MasterAppDb'].value | [0]" -o tsv)
fi
if [ -z "$PORTAL_CONNECTION_STRING" ]; then
  echo "✗ Could not read the production MasterAppDb connection string from $APP_NAME."
  exit 1
fi
ConnectionStrings__MasterAppDb="$PORTAL_CONNECTION_STRING" \
  dotnet ef database update \
    --project Infrastructure/Infrastructure.csproj \
    --startup-project AgentPortal/AgentPortal.csproj \
    --context MasterAppDbContext
unset PORTAL_CONNECTION_STRING

echo "▶ Zipping..."
cd "$PUBLISH_DIR"
zip -rq "$ZIP_PATH" .

echo "▶ Fetching deploy credentials..."
CREDS=$(az webapp deployment list-publishing-credentials \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --subscription "$SUBSCRIPTION" \
  --query "{u:publishingUserName,p:publishingPassword}" -o json)
DEPLOY_USER=$(echo "$CREDS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['u'])")
DEPLOY_PASS=$(echo "$CREDS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['p'])")

echo "▶ Deploying to $APP_NAME via Kudu zipdeploy..."
HTTP_CODE=$(curl -s -o /tmp/kudu-deploy-out.txt -w "%{http_code}" \
  -X POST \
  -u "${DEPLOY_USER}:${DEPLOY_PASS}" \
  -H "Content-Type: application/zip" \
  --data-binary @"$ZIP_PATH" \
  "https://${APP_NAME}.scm.azurewebsites.net/api/zipdeploy?isAsync=false&clean=true")

if [ "$HTTP_CODE" != "200" ]; then
  echo "✗ Deploy failed (HTTP $HTTP_CODE)"
  cat /tmp/kudu-deploy-out.txt
  exit 1
fi

echo "▶ Restarting app..."
az webapp restart \
  --subscription "$SUBSCRIPTION" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME"

echo "▶ Verifying deployed FFmpeg and FFprobe runtimes..."
FFMPEG_CHECK_PAYLOAD=$(python3 -c 'import json; print(json.dumps({"command": r"D:\\home\\site\\wwwroot\\tools\\ffmpeg\\ffmpeg.exe -version && D:\\home\\site\\wwwroot\\tools\\ffmpeg\\ffprobe.exe -version", "dir": r"D:\\home\\site\\wwwroot"}))')
FFMPEG_CHECK=$(curl -sS --fail \
  -u "${DEPLOY_USER}:${DEPLOY_PASS}" \
  -H "Content-Type: application/json" \
  --data "$FFMPEG_CHECK_PAYLOAD" \
  "https://${APP_NAME}.scm.azurewebsites.net/api/command")
printf "%s" "$FFMPEG_CHECK" | python3 -c 'import json,sys; result=json.load(sys.stdin); output=result.get("Output", ""); exit_code=result.get("ExitCode"); normalized=output.lower(); assert exit_code == 0 and "ffmpeg version" in normalized and "ffprobe version" in normalized, output'

echo "✓ Done → https://${APP_NAME}.azurewebsites.net"
