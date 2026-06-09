#!/usr/bin/env bash
set -euo pipefail

# ── AgentPortal → Azure Web App deployment ──────────────────────────────────
SUBSCRIPTION="LEGEND"
RESOURCE_GROUP="MASTERAPP-RG"
APP_NAME="masterapp-portal"
PROJECT="/Users/zacowen/MASTERAPP/AgentPortal/AgentPortal.csproj"
PUBLISH_DIR="/tmp/masterapp-portal-publish"
ZIP_PATH="/tmp/masterapp-portal.zip"

echo "▶ Setting subscription..."
az account set --subscription "$SUBSCRIPTION"

echo "▶ Cleaning previous build..."
rm -rf "$PUBLISH_DIR" "$ZIP_PATH"

echo "▶ Publishing..."
dotnet publish "$PROJECT" -c Release -o "$PUBLISH_DIR"

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

echo "✓ Done → https://${APP_NAME}.azurewebsites.net"
