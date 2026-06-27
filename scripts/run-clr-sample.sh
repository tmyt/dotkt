#!/usr/bin/env bash
# Compile + run a sample against the REAL pure-Kotlin clr-stdlib ONLY — no kotlin-stdlib.jar, no lossy facade meta.
# The sample is compiled TOGETHER with the clr-stdlib sources (runtime/stdlib/{common,src,unsigned}/src + clr actuals),
# so the frontend resolves builtins (kotlin.Any/Int/Unit/print) AND functions (listOf/getOrElse/...) from clr-stdlib's
# own declarations — faithfully (the --scan-asm facade path loses generic-signature fidelity; e.g. getOrElse misresolves).
# Then bir2cir -> ilemit -> a self-contained exe containing stdlib + main, and run it.
#
# This is the "Kotlin/CLR is a real Kotlin compiler shipping its own CLR stdlib" dev loop — used to drive the @Clr/BCL
# binding step (docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP"): a member still on a `TODO()`/abstract stub
# surfaces at runtime (e.g. EntryPointNotFoundException at kotlin.collections.List`1.get_size()).
#
#   scripts/run-clr-sample.sh <sample.kt> [AsmName]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
L="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
SAMPLE="${1:?usage: run-clr-sample.sh <sample.kt> [AsmName]}"
ASM="${2:-Sample}"
OUT="$ROOT/build/clr-sample"; BIR="$OUT/bir"; CIR="$OUT/cir"; DLL="$OUT/dll"
rm -rf "$BIR" "$CIR" "$DLL"; mkdir -p "$BIR" "$CIR" "$DLL"

[[ -x "$L" ]] || (cd "$ROOT" && ./gradlew -q :kotc:installDist)
[[ -f "$ROOT/build/bir2cir-bin/bir2cir.dll" ]] || dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
[[ -f "$ROOT/build/ilemit-bin/ilemit.dll" ]] || dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null

mapfile -t COMMON   < <(find "$ROOT/runtime/stdlib/common/src" -name '*.kt')
mapfile -t SRC      < <(find "$ROOT/runtime/stdlib/src" -name '*.kt')
mapfile -t UNSIGNED < <(find "$ROOT/runtime/stdlib/unsigned/src" -name '*.kt')
mapfile -t CLR      < <(find "$ROOT/runtime/stdlib/clr" -name '*.kt')
COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}")
COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"
OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi"

echo "== kotc: sample + ${#COMMON[@]}+${#SRC[@]}+${#UNSIGNED[@]}+${#CLR[@]} stdlib files -> BIR (no jar) =="
DOTKT_STDLIB_COMPILE=1 CLR_TYPES_METADATA="" "$L" \
  "${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" "${CLR[@]}" "$SAMPLE" \
  -no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
  -Xcommon-sources="$COMMON_CSV" $OPTIN -d "$BIR" 2>"$OUT/kotc.err"
echo "frontend errors: $(grep -c ': error:' "$OUT/kotc.err")   BIR files: $(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
grep ': error:' "$OUT/kotc.err" | head -8

if [[ "$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)" -gt 0 ]]; then
  echo "== bir2cir -> CIR =="
  DOTKT_STDLIB_COMPILE=1 dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" "$BIR"/*.bir.json 2>"$OUT/bir2cir.err" | tail -1
  echo "== ilemit -> $ASM.dll (self-contained: stdlib + main) =="
  DOTKT_STDLIB_COMPILE=1 dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$DLL" "$ASM" "$CIR"/*.cir.json 2>"$OUT/ilemit.err" | tail -2
  grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|error|unresolved|no matching|not found' | head -3
  if [[ -f "$DLL/$ASM.dll" ]]; then
    echo "== run =="
    dotnet "$DLL/$ASM.dll"
  fi
fi
