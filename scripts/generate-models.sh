#!/usr/bin/env bash
# * Regenerates DTOs + v2 envelope types from the canonical spec using NSwag.
# * Output is committed and machine-owned — never edit Dto.g.cs by hand.
# *
# * Requires the dotnet tool `nswag` installed locally:
# *   dotnet tool install --global NSwag.ConsoleCore
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SPEC="$ROOT/spec/v2.json"
OUT="$ROOT/src/CPlugin.SaaSWebApi.Models/Generated/Dto.g.cs"

if ! command -v nswag >/dev/null; then
  echo "nswag not on PATH — install via 'dotnet tool install --global NSwag.ConsoleCore'" >&2
  exit 1
fi
if [ ! -f "$SPEC" ]; then
  echo "spec missing at $SPEC — run scripts/fetch-spec.sh first" >&2
  exit 1
fi
mkdir -p "$(dirname "$OUT")"

# * DTO-only generation: the endpoint facade is produced separately by
#   scripts/generate-endpoints.sh (bespoke generator) on top of these types.
# * /arrayType List<T> (not ICollection<T>) so envelope `data` lists convert
#   cleanly to the facade's IReadOnlyList<T>/Page<T> returns.
nswag openapi2csclient \
  /input:"$SPEC" \
  /classname:"_Unused" \
  /namespace:"CPlugin.SaaSWebApi.Models" \
  /generateClientClasses:false \
  /generateClientInterfaces:false \
  /generateDtoTypes:true \
  /jsonLibrary:SystemTextJson \
  /generateNullableReferenceTypes:true \
  /generateOptionalPropertiesAsNullable:true \
  /generateDefaultValues:true \
  /arrayType:System.Collections.Generic.List \
  /arrayInstanceType:System.Collections.Generic.List \
  /dictionaryType:System.Collections.Generic.Dictionary \
  /generateDataAnnotations:false \
  /output:"$OUT"

echo "wrote $OUT"
