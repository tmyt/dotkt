#!/usr/bin/env bash
# Experiment (#119 concept-validation): build a minimal expect/actual MPP project (Greeter) for the
# CLR through the DotKt toolchain, to prove the kotc FRAGMENT machinery works for an ORDINARY user
# project — the same -Xexpect-actual-classes / -Xfragments=common,clr / -Xfragment-refines=clr:common
# the stdlib build uses, but WITHOUT the stdlib-only flags (-Xstdlib-compilation / -Xallow-kotlin-package).
# If this runs green it is the seed for DotKt.Sdk.Mpp: a real .ktproj needs the SDK MPP targets (#119),
# which this validates the underlying capability for.
#
#   kotc (common+clr fragments) -> BIR -> bir2cir -> CIR -> ilemit -> CIL -> run
#
# Expected output: "Hello from the CLR actual"
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/scripts/lib.sh"
HERE="$ROOT/experiments/mpp-greeter"
OUT="$HERE/out"; rm -rf "$OUT"; mkdir -p "$OUT"/{bir,cir,dll}

need_kotc; need_tool bir2cir; need_tool ilemit; need_fe_klib; need_stdlib_ref; need_stdlib_rt

# common = the expect-carrying, platform-agnostic source; clr = the actual + entry point (the sole
# platform impl). Authorship is the only difference; the compiler treats them by fragment.
COMMON=("$HERE/common/Greeter.kt")
CLR=("$HERE/clr/Greeter.kt" "$HERE/clr/Main.kt")
COMMON_CSV="$(IFS=,; echo "${COMMON[*]}")"

echo "== kotc (MPP: ${#COMMON[@]} common + ${#CLR[@]} clr) -> BIR =="
# The APP frontend pipeline (ClrAppFrontendPipelinePhase) splits common vs platform via
# isCommonSourceForPsi (driven by -Xcommon-sources) + -Xmulti-platform (enables the common module +
# cross-module expect/actual). The stdlib's -Xfragments/-Xfragment-sources is the STDLIB pipeline's
# mechanism and does not apply here. Sources passed positionally; -Xcommon-sources marks the common set.
"$KOTC" "${COMMON[@]}" "${CLR[@]}" -no-stdlib -classpath "$FE_KLIB" \
	-Xmulti-platform -Xexpect-actual-classes -Xcommon-sources="$COMMON_CSV" -d "$OUT/bir"

echo "== bir2cir -> CIR =="
dotnet "$BIR2CIR_DLL" "$OUT/cir" --ref "$STDLIB_REF_DLL" "$OUT"/bir/*.bir.json

echo "== ilemit -> CIL =="
dotnet "$ILEMIT_DLL" "$OUT/dll" mpp-greeter --ref "$STDLIB_RT_DLL" "$OUT"/cir/*.cir.json

echo "== run =="
cp "$STDLIB_RT_DLL" "$OUT/dll/"
cat > "$OUT/dll/mpp-greeter.runtimeconfig.json" <<'JSON'
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
JSON
echo "----"
( cd "$OUT/dll" && dotnet mpp-greeter.dll )
