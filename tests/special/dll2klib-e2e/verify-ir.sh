#!/usr/bin/env bash
# Validate this producer's fresh IR in its own shard, including subsidiary compilations.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
[[ $# == 1 && -d "$1" ]] || { echo "usage: verify-ir.sh <E2E output directory>" >&2; exit 2; }
mapfile -d '' -t artifacts < <(find "$1" -type f \( -name '*.bir.json' -o -name '*.cir.json' \) -print0)
(( ${#artifacts[@]} )) || { echo "E2E IR validation: no emitted artifacts" >&2; exit 1; }
"${PYTHON:-python3}" "$ROOT/scripts/verify-schema.py" "${artifacts[@]}"
"${PYTHON:-python3}" "$ROOT/scripts/verify-property-accessor-identity.py" "${artifacts[@]}"
