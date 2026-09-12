#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
python3 ../scripts/sync-published-checkout.py
dotnet run --project AgentPortal.csproj -c Debug
