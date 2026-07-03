#!/usr/bin/env bash
# * Downloads the live OpenAPI v2 spec from a running WebAPI into spec/v2.json.
# * Target host via WEBAPI_BASE_URL (default: the public staging host).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BASE_URL="${WEBAPI_BASE_URL:-https://pre.mywebapi.com}"
URL="${BASE_URL%/}/swagger/v2/swagger.json"

echo "fetching $URL"
curl -fsSL "$URL" -o "$ROOT/spec/v2.json"
echo "wrote $ROOT/spec/v2.json"
