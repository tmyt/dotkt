#!/usr/bin/env bash
# Auto-generate @Clr Kotlin façades from .NET type metadata (no hand-writing).
#   scripts/gen-facades.sh <outDir> <Type.Full.Name> [<Type.Full.Name> ...]
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?usage: gen-facades.sh <outDir> <Type>...}"; shift
dotnet build "$ROOT/tools/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" "$OUT" "$@"
