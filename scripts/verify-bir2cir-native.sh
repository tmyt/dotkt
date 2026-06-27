#!/usr/bin/env bash
# Focused bir2cir native-CIR regression check.
#
# The production path is still --compat-bir, but --native-cir is where the
# FIR -> BIR -> CIR split is being developed. This script verifies that the
# native draft keeps emitting resolved CLR calls/types and that compat mode
# remains byte-for-byte BIR-compatible.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build/bir2cir-native-verify"
NATIVE="$OUT/native"
COMPAT="$OUT/compat"

rm -rf "$OUT"
mkdir -p "$NATIVE" "$COMPAT"

dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
dotnet build "$ROOT/cases/ktproj-il/hello-il.ktproj" -v minimal --nologo >/dev/null

BIR="$ROOT/cases/ktproj-il/obj/dotkt-bir/App.bir.json"
REF="$ROOT/cases/ktproj-il/bin/Debug/net10.0/hello-il.dll"

if [[ ! -f "$BIR" ]]; then
    echo "FAIL  missing BIR fixture: $BIR" >&2
    exit 1
fi
if [[ ! -f "$REF" ]]; then
    echo "FAIL  missing reference assembly: $REF" >&2
    exit 1
fi

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$COMPAT" "$BIR" >/dev/null
cmp -s "$BIR" "$COMPAT/App.cir.json"

dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$NATIVE" --native-cir --ref "$REF" "$BIR" >/dev/null
CIR="$NATIVE/App.cir.json"

require() {
    local pattern="$1" label="$2"
    if ! rg -q "$pattern" "$CIR"; then
        echo "FAIL  native CIR missing $label ($pattern)" >&2
        exit 1
    fi
}

require '"typeSites"' "type site inventory"
require '"typeResolutionDraft"' "type resolution draft"
require '"resolvedCalls"' "resolved call draft"
require '"resolvedTypes"' "resolved type draft"
require '"loweredBir"' "lowered BIR draft"
require '"k": "clr.newobj"' "lowered constructor call"
require '"k": "clr.call"' "lowered method call"
require '"k": "clr.typeRef"' "lowered type reference"
require '"sourcePath": "\$\.methods\[0\]\.body\[0\]\.init"' "constructor source path"
require '"sourcePath": "\$\.methods\[0\]\.body\[1\]\.expr\.args\[0\]"' "method source path"

echo "PASS  bir2cir native draft and compat identity"
