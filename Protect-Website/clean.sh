#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
dotnet clean ProtectWebsite.csproj -c Debug
