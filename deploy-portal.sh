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

echo "▶ Deploying to $APP_NAME..."
az webapp deploy \
  --subscription "$SUBSCRIPTION" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --src-path "$ZIP_PATH" \
  --type zip \
  --clean true \
  --restart true \
  --async false \
  --timeout 600

echo "✓ Done → https://${APP_NAME}.azurewebsites.net"
