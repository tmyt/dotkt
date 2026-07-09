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
FE_KLIB="$ROOT/build/clr-stdlib-frontend-klib/kotlin-stdlib-clr-frontend.klib"
# Backward-compatible variable name for scripts that still talk about the old frontend jar.
FE_JAR="$FE_KLIB"
STDLIB_REF_DLL="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_RT_DLL="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"

# --- logging ---------------------------------------------------------------------------------------
info() { echo "$SCRIPT_NAME: $*"; }
warn() { echo "$SCRIPT_NAME: warning: $*" >&2; }
die()  { echo "$SCRIPT_NAME: error: $*" >&2; exit 1; }
# Flag-parsing convention: usage() prints a heredoc; parse with `while (( $# )); do case ... esac; done`;
# every script answers -h|--help; unknown flags go through usage_error.
usage_error() { echo "$SCRIPT_NAME: $*" >&2; usage >&2; exit 2; }

# --- XFAIL baseline verdict (shared by the verify gates) --------------------------------------------
# A gate declares its expected failures as an associative array (fail name -> reason) and hands its
# ACTUAL fail names to xfail_diff, which prints one classification line per name:
#   XFAIL     <prefix>:<name> (<reason>)    expected fail, still failing — does NOT redden the gate
#   NEW-FAIL  <prefix>:<name>               fail NOT in the baseline — a regression
#   FIXED     <prefix>:<name> — fixed; remove it from the xfail list   (green; prune the entry)
# Every NEW-FAIL is appended to the global XFAIL_NEW array; the caller's final verdict is
# exit 0 iff XFAIL_NEW is empty after all xfail_diff calls.
declare -a XFAIL_NEW=()
xfail_diff() { # <prefix> <xfail-assoc-array-name> [actual-fail-name...]
	local _pfx="$1"; local -n _xf="$2"; shift 2
	local n
	for n in "$@"; do
		if [[ -v _xf[$n] ]]; then
			echo "XFAIL     $_pfx:$n (${_xf[$n]})"
		else
			echo "NEW-FAIL  $_pfx:$n"; XFAIL_NEW+=("$_pfx:$n")
		fi
	done
	for n in $(printf '%s\n' "${!_xf[@]}" | sort); do
		if [[ " ${*:-} " != *" $n "* ]]; then
			echo "FIXED     $_pfx:$n — fixed; remove it from the xfail list"
		fi
	done
}

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
need_fe_klib() { # the CLR frontend stdlib KLIB (kotc -classpath input); consumes the kotc install's lib jars
	[[ -e "$FE_KLIB" ]] || { info "building CLR frontend stdlib klib" >&2; bash "$ROOT/scripts/build-stdlib-klib.sh" >/dev/null 2>&1; }
}
need_fe_jar() { need_fe_klib; }
need_stdlib_ref() { [[ -f "$STDLIB_REF_DLL" ]] || { info "building stdlib REFERENCE dll" >&2; bash "$ROOT/scripts/build-stdlib-ref.sh" --emit >/dev/null 2>&1; }; }
need_stdlib_rt()  { [[ -f "$STDLIB_RT_DLL"  ]] || { info "building stdlib RUNTIME dll"   >&2; bash "$ROOT/scripts/build-stdlib-rt.sh"  --emit >/dev/null 2>&1; }; }

# --- stdlib source sets (shared by build-stdlib-ref.sh / build-stdlib-rt.sh) -----------------------
# Sets the arrays STDLIB_COMMON/STDLIB_SRC/STDLIB_UNSIGNED/STDLIB_CLR and STDLIB_COMMON_CSV.
# Common = the multiplatform expect/impl source; Platform(CLR) = the clr/ actuals (NOT common sources).
collect_stdlib_sources() {
	mapfile -t STDLIB_COMMON   < <(find "$ROOT/libraries/stdlib/common/src" -name '*.kt')
	mapfile -t STDLIB_SRC      < <(find "$ROOT/libraries/stdlib/src" -name '*.kt')
	mapfile -t STDLIB_UNSIGNED < <(find "$ROOT/libraries/stdlib/unsigned/src" -name '*.kt')
	mapfile -t STDLIB_CLR      < <(find "$ROOT/libraries/stdlib/clr" -name '*.kt')
	local all=("${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}")
	STDLIB_COMMON_CSV="$(IFS=,; echo "${all[*]}")"
}
stdlib_fragment_args() {
	STDLIB_FRAGMENT_ARGS=(-Xfragments=common,clr -Xfragment-refines=clr:common)
	local f
	for f in "${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}"; do
		STDLIB_FRAGMENT_ARGS+=("-Xfragment-sources=common:$f")
	done
	for f in "${STDLIB_CLR[@]}"; do
		STDLIB_FRAGMENT_ARGS+=("-Xfragment-sources=clr:$f")
	done
}
# The stdlib compile's opt-ins + frontend flags (identical for the ref and rt builds).
STDLIB_OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi"
