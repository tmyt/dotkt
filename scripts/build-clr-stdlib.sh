#!/usr/bin/env bash
# Build the REAL pure-Kotlin Kotlin/CLR standard library: compile the multiplatform common source
# (runtime/stdlib/common/src + runtime/stdlib/src + runtime/stdlib/unsigned/src)
# against the CLR platform `actual`s (runtime/stdlib/clr), emit BIR,
# then ilemit -> DotKt.Stdlib.dll. Phase 1 of docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP" (stubs =TODO()).
#
#   scripts/build-clr-stdlib.sh [--emit]   # --emit also runs ilemit; default = frontend+BIR only (faster triage)
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
L="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
OUT="$ROOT/build/clr-stdlib"; BIR="$OUT/bir"; CIR="$OUT/cir"; DLL="$OUT/dll"
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
do_emit=0; [[ "${1:-}" == "--emit" ]] && do_emit=1

[[ -x "$L" ]] || (cd "$ROOT" && ./gradlew -q :kotc:installDist)
rm -rf "$BIR"; mkdir -p "$BIR"

# Common = the multiplatform expect/impl source; Platform(CLR) = the clr/ actuals (NOT in -Xcommon-sources).
mapfile -t COMMON < <(find "$ROOT/runtime/stdlib/common/src" -name '*.kt')
mapfile -t SRC < <(find "$ROOT/runtime/stdlib/src" -name '*.kt')
mapfile -t UNSIGNED < <(find "$ROOT/runtime/stdlib/unsigned/src" -name '*.kt')
mapfile -t CLR < <(find "$ROOT/runtime/stdlib/clr" -name '*.kt')
COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}")
COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"

OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi,kotlin.ExperimentalUnsignedTypes"
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xcommon-sources="$COMMON_CSV" $OPTIN)

echo "== kotc: ${#COMMON[@]} common + ${#SRC[@]} src + ${#UNSIGNED[@]} unsigned + ${#CLR[@]} clr -> BIR =="
DOTKT_STDLIB_COMPILE=1 CLR_TYPES_METADATA="" "$L" "${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" "${CLR[@]}" "${FLAGS[@]}" -d "$BIR" 2> $OUT/kotc.err
echo "frontend errors: $(grep -c ': error:' "$OUT/kotc.err")   BIR files: $(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
echo "--- top error kinds ---"
grep ': error:' "$OUT/kotc.err" | sed -E 's/^.*: error: //; s/'"'"'[^'"'"']*'"'"'/X/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -15

if (( do_emit )) && [[ "$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)" -gt 0 ]]; then
  [[ -f "$ROOT/build/bir2cir-bin/bir2cir.dll" ]] || dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
  rm -rf "$CIR" "$DLL"; mkdir -p "$CIR" "$DLL"
  echo "== bir2cir -> CIR =="
  DOTKT_STDLIB_COMPILE=1 dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" --ref "$DOTKT_RT" "$BIR"/*.bir.json 2>"$OUT/bir2cir.err"
  echo "CIR files: $(ls "$CIR"/*.cir.json 2>/dev/null | wc -l)"
  echo "== ilemit(CIR compat) -> DotKt.Stdlib.dll =="
  DOTKT_STDLIB_COMPILE=1 dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$DLL" DotKt.Stdlib --ref "$DOTKT_RT" "$CIR"/*.cir.json 2>"$OUT/ilemit.err" | tail -2
  grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|KeyNot|unresolved|no matching' | head -3
  ls -la "$DLL"/DotKt.Stdlib.dll 2>/dev/null && echo "*** DotKt.Stdlib.dll emitted ***"
fi
