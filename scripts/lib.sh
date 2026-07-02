# scripts/lib.sh — shared conventions for every script in scripts/. Source it, never execute it:
#
#   source "$(dirname "$0")/lib.sh"
#
# Provides, in order: strict mode (set -euo pipefail — a script that must tolerate a failing
# command, e.g. kotc exiting nonzero while the script reports error counts, adds an explicit
# `|| true` at that call site); ROOT; the canonical toolchain/artifact paths (the single source —
# the Makefile mirrors these, do not re-derive them in a script); log helpers info/warn/die with
# a uniform "<script>:" prefix; usage_error() (scripts define usage()); and lazy need_*() builders
# that build a missing tool/artifact, loudly.

[[ -n "${BASH_VERSION:-}" ]] || { echo "lib.sh: bash required" >&2; exit 2; }
set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

SCRIPT_NAME="${SCRIPT_NAME:-$(basename -- "$0")}"
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"

# --- canonical artifact paths (single source) ------------------------------------------------------
KOTC="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
ILEMIT_DLL="$ROOT/build/ilemit-bin/ilemit.dll"
BIR2CIR_DLL="$ROOT/build/bir2cir-bin/bir2cir.dll"
FACADEGEN_DLL="$ROOT/build/facadegen-bin/facadegen.dll"
RETARGET_DLL="$ROOT/build/retarget-bin/retarget.dll"
FE_JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
STDLIB_REF_DLL="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_RT_DLL="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"

# --- logging ---------------------------------------------------------------------------------------
info() { echo "$SCRIPT_NAME: $*"; }
warn() { echo "$SCRIPT_NAME: warning: $*" >&2; }
die()  { echo "$SCRIPT_NAME: error: $*" >&2; exit 1; }
# Flag-parsing convention: usage() prints a heredoc; parse with `while (( $# )); do case ... esac; done`;
# every script answers -h|--help; unknown flags go through usage_error.
usage_error() { echo "$SCRIPT_NAME: $*" >&2; usage >&2; exit 2; }

# --- lazy builders (loud when they fire) -----------------------------------------------------------
need_kotc() {
	[[ -x "$KOTC" ]] || { info "building kotc (gradlew :kotc:installDist)" >&2; (cd "$ROOT" && ./gradlew -q :kotc:installDist); }
}
need_tool() { # <name> — ensure build/<name>-bin/<name>.dll exists (ilemit|bir2cir|facadegen|retarget); lazy
	local t="$1" dll="$ROOT/build/$1-bin/$1.dll"
	[[ -f "$dll" ]] || { info "building $t" >&2; dotnet build "$ROOT/toolchain/$t" -c Release -o "$ROOT/build/$t-bin" -v q --nologo >/dev/null; }
}
build_tool() { # <name> — UNCONDITIONAL build (the verify gates use this: they must test the CURRENT sources)
	dotnet build "$ROOT/toolchain/$1" -c Release -o "$ROOT/build/$1-bin" -v q --nologo >/dev/null
}
need_fe_jar() { # the CLR frontend stdlib jar (kotc -classpath input); consumes the kotc install's lib jars
	[[ -f "$FE_JAR" ]] || { info "building CLR frontend stdlib jar" >&2; bash "$ROOT/scripts/build-stdlib-jar.sh" >/dev/null 2>&1; }
}
need_stdlib_ref() { [[ -f "$STDLIB_REF_DLL" ]] || { info "building stdlib REFERENCE dll" >&2; bash "$ROOT/scripts/build-stdlib-ref.sh" --emit >/dev/null 2>&1; }; }
need_stdlib_rt()  { [[ -f "$STDLIB_RT_DLL"  ]] || { info "building stdlib RUNTIME dll"   >&2; bash "$ROOT/scripts/build-stdlib-rt.sh"  --emit >/dev/null 2>&1; }; }

# --- stdlib source sets (shared by build-stdlib-ref.sh / build-stdlib-rt.sh) -----------------------
# Sets the arrays STDLIB_COMMON/STDLIB_SRC/STDLIB_UNSIGNED/STDLIB_CLR and STDLIB_COMMON_CSV.
# Common = the multiplatform expect/impl source; Platform(CLR) = the clr/ actuals (NOT common sources).
collect_stdlib_sources() {
	mapfile -t STDLIB_COMMON   < <(find "$ROOT/runtime/stdlib/common/src" -name '*.kt')
	mapfile -t STDLIB_SRC      < <(find "$ROOT/runtime/stdlib/src" -name '*.kt')
	mapfile -t STDLIB_UNSIGNED < <(find "$ROOT/runtime/stdlib/unsigned/src" -name '*.kt')
	mapfile -t STDLIB_CLR      < <(find "$ROOT/runtime/stdlib/clr" -name '*.kt')
	local all=("${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}")
	STDLIB_COMMON_CSV="$(IFS=,; echo "${all[*]}")"
}
# The stdlib compile's opt-ins + frontend flags (identical for the ref and rt builds).
STDLIB_OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi"
