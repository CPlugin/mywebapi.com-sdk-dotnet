#!/usr/bin/env bash
# * Regenerates the MT4/MT5 endpoint facades from the canonical spec.
#   Run after scripts/generate-models.sh whenever spec/v2.json changes.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet run --project "$ROOT/scripts/GenerateEndpoints" -- \
  "$ROOT/spec/v2.json" \
  "$ROOT/src/CPlugin.SaaSWebApi.Client/Generated"
