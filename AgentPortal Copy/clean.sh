#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
dotnet clean AgentPortal.csproj -c Debug
