#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FIXTURE="$ROOT/tests/roundtrip/malformed-companion-fixtures/MalformedCompanionFixtures.csproj"
DLL2KLIB="$ROOT/toolchain/dll2klib/bin/Debug/net10.0/dll2klib.dll"
BIR2CIR="$ROOT/toolchain/bir2cir/bin/Debug/net10.0/bir2cir.dll"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DOTNET_BIN="$(readlink -f "$(command -v dotnet)")"
DOTNET_REF_PACK="$(dirname "$DOTNET_BIN")/packs/Microsoft.NETCore.App.Ref"
CORE_REF="$(find "$DOTNET_REF_PACK" -path '*/ref/net10.0/System.Runtime.dll' -print | sort -V | tail -1)"
[[ -n "$CORE_REF" ]] || { echo "companion-negative: System.Runtime reference assembly not found" >&2; exit 1; }

BIR="$WORK/probe.bir.json"
printf '%s\n' '{"fileClass":"CompanionNegativeProbeKt","hasMain":false,"fields":[],"methods":[],"types":[]}' >"$BIR"

run_rejected() {
    local tool="$1"
    local dll="$2"
    local expected="$3"
    local log="$WORK/$(basename "$dll").$tool.log"
    if [[ "$tool" == "dll2klib" ]]; then
        printf '%s\n' "$dll" >"$WORK/refs.rsp"
        if dotnet "$DLL2KLIB" --out "$WORK/klib" "@$WORK/refs.rsp" >"$log" 2>&1; then
            echo "companion-negative: dll2klib accepted malformed trusted carrier $dll" >&2
            return 1
        fi
    elif dotnet "$BIR2CIR" "$WORK/cir" --compile-refs "$CORE_REF;$dll" "$BIR" >"$log" 2>&1; then
        echo "companion-negative: bir2cir accepted malformed trusted carrier $dll" >&2
        return 1
    fi
    if ! grep -Eqi "$expected" "$log"; then
        echo "companion-negative: $tool rejected malformed metadata for the wrong reason" >&2
        tail -20 "$log" >&2
        return 1
    fi
}

build_fixture() {
    local name="$1" define="$2" out="$WORK/$1"
    dotnet build "$FIXTURE" -v:q --nologo \
        -p:AssemblyName="$name" \
        -p:DefineConstants="$define" \
        -p:BaseIntermediateOutputPath="$out/obj/" \
        -p:OutputPath="$out/bin/"
}

build_fixture MalformedCompanionCarrier ''
NON_NESTED="$WORK/MalformedCompanionCarrier/bin/MalformedCompanionCarrier.dll"
run_rejected dll2klib "$NON_NESTED" 'ordinary nested'
run_rejected bir2cir "$NON_NESTED" 'ordinary nested'

BAD_NAME="$WORK/MalformedCompanionCarrier/bin/BadSemanticNameCarrier.dll"
cp "$NON_NESTED" "$BAD_NAME"
sed -i 's/"name":"Companion"/"name":"Bad.Name!"/g' "$BAD_NAME"
run_rejected dll2klib "$BAD_NAME" 'semantic name segment'
run_rejected bir2cir "$BAD_NAME" 'semantic owner/name'

build_fixture NonPublicCompanionCarrier NON_PUBLIC_CARRIER
NON_PUBLIC="$WORK/NonPublicCompanionCarrier/bin/NonPublicCompanionCarrier.dll"
# C# cannot spell '$' in an identifier. Keep the fixture otherwise structurally valid and make the same-length
# metadata-string substitution so an implementation that wrongly accepts NestedFamily reaches the singleton check.
sed -i 's/XINSTANCE/$INSTANCE/g' "$NON_PUBLIC"
run_rejected dll2klib "$NON_PUBLIC" 'NestedPublic visibility'
run_rejected bir2cir "$NON_PUBLIC" 'NestedPublic visibility'

build_fixture ConstrainedCompanionCarrier CONSTRAINED_CARRIER
CONSTRAINED="$WORK/ConstrainedCompanionCarrier/bin/ConstrainedCompanionCarrier.dll"
sed -i 's/XINSTANCE/$INSTANCE/g' "$CONSTRAINED"
run_rejected dll2klib "$CONSTRAINED" 'generic captures must be unconstrained'
run_rejected bir2cir "$CONSTRAINED" 'generic captures must be unconstrained'

echo "non-nested, malformed-name, non-public, and constrained trusted companion carriers rejected by dll2klib + bir2cir: OK"
