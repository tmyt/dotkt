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
need_stdlib_ref
need_stdlib_rt
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

# Only the small probe BIR is emitted. The normal canonical stdlib already supplies
# every other declaration, so the runtime check does not build a second runtime stdlib.
bir_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$STDLIB_RT_DLL")"
emit_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")"
rm -rf -- "$OUT/cir" "$OUT/dll"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$bir_refs" "$OUT/bir/SourceArrayProbe.bir.json" \
    > "$OUT/bir2cir.log" 2>&1
dotnet "$ILEMIT_DLL" "$OUT/dll" SourceArrayProbe --compile-refs "$emit_refs" \
    --runtime-refs "$STDLIB_RT_DLL" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
    "$OUT/cir/SourceArrayProbe.cir.json" > "$OUT/ilemit.log" 2>&1
cp "$STDLIB_RT_DLL" "$OUT/dll/"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/dll/SourceArrayProbe.dll"
dotnet build "$ROOT/tests/stdlib-source-array/RuntimeProbe.csproj" -c Release -o "$OUT/runner" \
    -v q --nologo > "$OUT/runtime-build.log" 2>&1
actual="$(dotnet "$OUT/runner/RuntimeProbe.dll" "$OUT/dll/SourceArrayProbe.dll" "$STDLIB_RT_DLL")"
[[ "$actual" == source-array-runtime-ok ]] || die "runtime probe did not complete: $actual"

# This tiny source-owned builtin exercises the same checker split without another
# whole-stdlib compile. Each invalid Kotlin form must still produce its diagnostic.
negative_status=0
env -u DOTKT_BUILD_KLIB "$KOTC" -Xallow-kotlin-package \
    -classpath "$FE_KLIB:${STDLIB_REFERENCE_ARGS[1]}" \
    "$ROOT/tests/stdlib-source-array/NegativeProbe.kt" -d "$OUT/negative" \
    > "$OUT/negative.log" 2>&1 || negative_status=$?
[[ "$negative_status" == 1 ]] || die "negative probe exited $negative_status instead of normal compilation failure"
python3 "$ROOT/tests/stdlib-source-array/check-diagnostics.py" "$OUT/negative.log"
echo "stdlib-source-array: GREEN"
