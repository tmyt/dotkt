#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DOTNET_BIN="$(readlink -f "$(command -v dotnet)")"
DOTNET_REF_PACK="$(dirname "$DOTNET_BIN")/packs/Microsoft.NETCore.App.Ref"
CORE_REF="$(find "$DOTNET_REF_PACK" -path '*/ref/net10.0/System.Runtime.dll' -print | sort -V | tail -1)"
[[ -n "$CORE_REF" ]] || { echo "flags-lookalike: System.Runtime reference assembly not found" >&2; exit 1; }

FAKE_DLL="$WORK/FlagsLookalike.dll"
dotnet run --project "$ROOT/tests/roundtrip/flags-lookalike-fixture/FlagsLookalikeGenerator.csproj" \
    -v:q -- "$FAKE_DLL"
printf '%s\n%s\n' "$FAKE_DLL" "$CORE_REF" >"$WORK/refs.rsp"
dotnet "$ROOT/build/dll2klib-bin/dll2klib.dll" --out "$WORK/klib" "@$WORK/refs.rsp" >/dev/null
dotnet "$ROOT/tests/roundtrip/metadata-inspector/bin/Debug/net10.0/CompanionMetadataInspector.dll" \
    --klib-no-flags-enum "$WORK/klib/FlagsLookalike.klib" Lookalike.FakeFlags
