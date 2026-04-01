#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
dotnet run --project ProtectWebsite.csproj -c Debug
