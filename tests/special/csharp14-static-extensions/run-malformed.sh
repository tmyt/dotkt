#!/usr/bin/env bash
set -euo pipefail

SCRIPT_NAME="$(basename -- "$0")"
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd -P)/scripts/lib.sh"
need_tool dll2klib
need_tool bir2cir
need_stdlib_ref

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
GENERATOR="$ROOT/tests/special/csharp14-static-extensions/malformed/StaticExtensionMalformedGenerator.csproj"
DOTNET_BIN="$(readlink -f "$(command -v dotnet)")"
DOTNET_REF_PACK="$(dirname "$DOTNET_BIN")/packs/Microsoft.NETCore.App.Ref"
CORE_REF="$(find "$DOTNET_REF_PACK" -path '*/ref/net10.0/System.Runtime.dll' -print | sort -V | tail -1)"
[[ -n "$CORE_REF" ]] || { echo "csharp14-malformed: System.Runtime reference assembly not found" >&2; exit 1; }

BIR="$WORK/probe.bir.json"
printf '%s\n' '{"fileClass":"CSharp14MalformedProbeKt","hasMain":false,"fields":[],"methods":[],"types":[]}' >"$BIR"

generate() {
    local mode="$1"
    local output="$WORK/$mode.dll"
    dotnet run --project "$GENERATOR" -v:q -- "$output" "$mode"
    printf '%s\n' "$output"
}

accept() {
    local dll="$1"
    printf '%s\n' "$dll" >"$WORK/refs.rsp"
    dotnet "$DLL2KLIB_DLL" --out "$WORK/valid-klib" "@$WORK/refs.rsp" \
        >"$WORK/valid.dll2klib.log" 2>&1
    dotnet "$BIR2CIR_DLL" "$WORK/valid-cir" --compile-refs "$CORE_REF;$STDLIB_REF_DLL;$dll" "$BIR" \
        >"$WORK/valid.bir2cir.log" 2>&1
}

reject() {
    local tool="$1" dll="$2" mode="$3" expected="$4"
    local log="$WORK/$mode.$tool.log"
    if [[ "$tool" == "dll2klib" ]]; then
        printf '%s\n' "$dll" >"$WORK/refs.rsp"
        if dotnet "$DLL2KLIB_DLL" --out "$WORK/$mode-klib" "@$WORK/refs.rsp" >"$log" 2>&1; then
            echo "csharp14-malformed: dll2klib accepted $mode" >&2
            return 1
        fi
    elif dotnet "$BIR2CIR_DLL" "$WORK/$mode-cir" --compile-refs "$CORE_REF;$STDLIB_REF_DLL;$dll" "$BIR" >"$log" 2>&1; then
        echo "csharp14-malformed: bir2cir accepted $mode" >&2
        return 1
    fi
    if ! grep -Eqi "$expected" "$log"; then
        echo "csharp14-malformed: $tool rejected $mode for the wrong reason" >&2
        tail -20 "$log" >&2
        return 1
    fi
}

valid="$(generate valid)"
accept "$valid"

for mode in missing-marker duplicate-implementation signature-mismatch callable-declaration callable-marker; do
    dll="$(generate "$mode")"
    case "$mode" in
        missing-marker) expected='receiver marker.*resolve' ;;
        duplicate-implementation) expected='resolves to 2 implementation' ;;
        signature-mismatch) expected='resolves to 0 implementation' ;;
        callable-declaration) expected='declaration.*callable' ;;
        callable-marker) expected='invalid marker method|invalid signature or body' ;;
    esac
    reject dll2klib "$dll" "$mode" "$expected"
    reject bir2cir "$dll" "$mode" "$expected"
done

echo "PASS  malformed C# 14 static extension graphs rejected by dll2klib + bir2cir"
