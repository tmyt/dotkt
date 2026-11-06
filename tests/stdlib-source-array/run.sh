#!/usr/bin/env bash
set -euo pipefail
SCRIPT_NAME=stdlib-source-array
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"

# This source-owned path is not exercised by application tests reading the stdlib KLIB.
# Compile every family in one invocation, and reuse the canonical frontend KLIB for
# metadata checks rather than compiling the whole stdlib a second time.
OUT="$ROOT/build/tests-stdlib-source-array"
mkdir -p "$OUT"
need_kotc
need_fe_klib
collect_stdlib_sources
STDLIB_CLR+=("$ROOT/tests/stdlib-source-array/SourceArrayProbe.kt")
stdlib_fragment_args
stdlib_reference_args
rm -rf -- "$OUT/bir"

env -u DOTKT_BUILD_KLIB "$KOTC" \
    "${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
    -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
    -Xreturn-value-checker=check -XXLanguage:+UnnamedLocalVariables \
    -Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN \
    "${STDLIB_FRAGMENT_ARGS[@]}" "${STDLIB_REFERENCE_ARGS[@]}" \
    -d "$OUT/bir" > "$OUT/compiler.log" 2>&1 || {
        tail -60 "$OUT/compiler.log"
        exit 1
    }
python3 "$ROOT/tests/stdlib-source-array/check-bir.py" "$OUT/bir"
java --class-path "$ROOT/toolchain/kotc/build/install/kotc/lib/*" \
    "$ROOT/tests/stdlib-source-array/ArrayMetadataProbe.java" "$FE_KLIB"
echo "stdlib-source-array: GREEN"
