#!/usr/bin/env bash
# Build the RUNTIME stdlib assembly (the ref/runtime split's impl side). Same sources as build-clr-stdlib.sh, but in
# SUBSTITUTE mode (DOTKT_STDLIB_SUBSTITUTE=1): clrName is ACTIVE, so the @Clr annotations in the sources bind
# List->IReadOnlyList, size->Count, get->get_Item etc. The @Clr-bound TYPES then resolve to the BCL and are NOT emitted
# (no clash with the ref's pure-Kotlin shapes); the stdlib FUNCTIONS (listOf/map/filter/asList) are emitted with
# substituted signatures. ref + runtime share the assembly name (DotKt.Stdlib) -> compile-against-ref / run-against-runtime.
# docs/design-clr-stdlib-ref-runtime-split.md "Runtime-build architecture".
#
#   scripts/build-clr-stdlib-runtime.sh [--emit]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
L="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
OUT="$ROOT/build/clr-stdlib-rt"; BIR="$OUT/bir"; CIR="$OUT/cir"; DLL="$OUT/dll"
rm -rf "$BIR" "$CIR" "$DLL"; mkdir -p "$BIR" "$CIR" "$DLL"
do_emit=0; [[ "${1:-}" == "--emit" ]] && do_emit=1
[[ -x "$L" ]] || (cd "$ROOT" && ./gradlew -q :kotc:installDist)

mapfile -t COMMON   < <(find "$ROOT/runtime/stdlib/common/src" -name '*.kt')
mapfile -t SRC      < <(find "$ROOT/runtime/stdlib/src" -name '*.kt')
mapfile -t UNSIGNED < <(find "$ROOT/runtime/stdlib/unsigned/src" -name '*.kt')
mapfile -t CLR      < <(find "$ROOT/runtime/stdlib/clr" -name '*.kt')
COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}"); COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"
OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi"
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xcommon-sources="$COMMON_CSV" $OPTIN)

echo "== SUBSTITUTE-mode kotc: ${#COMMON[@]}+${#SRC[@]}+${#UNSIGNED[@]}+${#CLR[@]} stdlib files -> BIR (@Clr ACTIVE) =="
DOTKT_STDLIB_COMPILE=1 DOTKT_STDLIB_SUBSTITUTE=1 CLR_TYPES_METADATA="" "$L" "${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" "${CLR[@]}" "${FLAGS[@]}" -d "$BIR" 2>"$OUT/kotc.err"
echo "frontend errors: $(grep -c ': error:' "$OUT/kotc.err")   BIR files: $(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
grep ': error:' "$OUT/kotc.err" | sed -E 's/^.*: error: //; s/'"'"'[^'"'"']*'"'"'/X/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -10

if (( do_emit )) && [[ "$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)" -gt 0 ]]; then
  [[ -f "$ROOT/build/bir2cir-bin/bir2cir.dll" ]] || dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
  [[ -f "$ROOT/build/ilemit-bin/ilemit.dll" ]] || dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null
  echo "== bir2cir (substitute) -> CIR =="
  DOTKT_STDLIB_COMPILE=1 DOTKT_STDLIB_SUBSTITUTE=1 dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" "$BIR"/*.bir.json 2>"$OUT/bir2cir.err" | tail -1
  echo "== ilemit (substitute) -> DotKt.Stdlib.dll =="
  DOTKT_STDLIB_COMPILE=1 DOTKT_STDLIB_SUBSTITUTE=1 dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$DLL" DotKt.Stdlib "$CIR"/*.cir.json 2>"$OUT/ilemit.err" | tail -2
  grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|error|unresolved|no matching|not found|cannot' | head -3
fi
