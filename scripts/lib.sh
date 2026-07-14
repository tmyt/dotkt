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
# --- toolchain fingerprint: rebuild a stdlib artifact when the toolchain that BAKED it changed --------
# (GitHub #13) need_fe_klib/need_stdlib_ref/need_stdlib_rt used to rebuild ONLY when the artifact was
# MISSING — so a kotlin-stdlib-clr-frontend.klib / DotKt.Private.Stdlib.dll / DotKt.Stdlib.dll baked by an
# OLDER toolchain (or left by another branch's build) was silently reused against a NEWER compile: a
# false-RED, and — worse — a silent stale-GREEN (a case passing against a stale bake, breaking on the next
# fresh one). Each of the three artifacts is a deterministic function of its INPUTS (the installed kotc, the
# relevant native tool dlls, and the stdlib source tree); we hash those inputs into a sidecar
# '<artifact>.toolstamp' and rebuild on MISMATCH, not just absence. The hash is mtime+size+path (cheap; the
# build tools are incremental, so an unchanged toolchain leaves every input untouched -> stamp matches -> no
# spurious rebuild, preserving the build-only-if-needed fast path).
KOTC_INSTALL_DIR="$ROOT/toolchain/kotc/build/install/kotc"
STDLIB_SRC_DIR="$ROOT/libraries/stdlib"

# _toolstamp <path>... — fingerprint the given input files/dirs. Missing paths contribute nothing (a build
# that truly lacks an input fails loudly on its own); deterministic (sort before hashing).
_toolstamp() { find "$@" -type f -printf '%T@ %s %p\n' 2>/dev/null | LC_ALL=C sort | sha256sum | awk '{print $1}'; }
# Per-artifact input sets. klib: kotc + stdlib sources (a klib has no IL -> ilemit/bir2cir are irrelevant to
# its bytes). ref: kotc + bir2cir + ilemit + retarget + sources. rt: kotc + bir2cir + ilemit + the REF dll it
# consumes (bir2cir --ref) + sources.
_toolstamp_klib() { _toolstamp "$KOTC_INSTALL_DIR" "$STDLIB_SRC_DIR"; }
_toolstamp_ref()  { _toolstamp "$KOTC_INSTALL_DIR" "$BIR2CIR_DLL" "$ILEMIT_DLL" "$RETARGET_DLL" "$STDLIB_SRC_DIR"; }
_toolstamp_rt()   { _toolstamp "$KOTC_INSTALL_DIR" "$BIR2CIR_DLL" "$ILEMIT_DLL" "$STDLIB_REF_DLL" "$STDLIB_SRC_DIR"; }
# _stamp_fresh <artifact> <fingerprint>: true iff the artifact exists AND its sidecar records this fingerprint.
_stamp_fresh() { [[ -e "$1" && -f "$1.toolstamp" && "$(cat "$1.toolstamp" 2>/dev/null)" == "$2" ]]; }

need_fe_klib() { # the CLR frontend stdlib KLIB (kotc -classpath input); rebuild if missing OR toolchain changed
	_stamp_fresh "$FE_KLIB" "$(_toolstamp_klib)" && return 0
	info "building CLR frontend stdlib klib (missing or toolchain fingerprint changed)" >&2
	bash "$ROOT/scripts/build-stdlib-klib.sh" >/dev/null 2>&1
	# Recompute post-build: build-stdlib-klib.sh may itself build kotc (need_kotc), moving input mtimes.
	_toolstamp_klib > "$FE_KLIB.toolstamp"
}
need_stdlib_ref() { # the stdlib REFERENCE dll; rebuild if missing OR toolchain changed
	_stamp_fresh "$STDLIB_REF_DLL" "$(_toolstamp_ref)" && return 0
	info "building stdlib REFERENCE dll (missing or toolchain fingerprint changed)" >&2
	bash "$ROOT/scripts/build-stdlib-ref.sh" --emit >/dev/null 2>&1
	_toolstamp_ref > "$STDLIB_REF_DLL.toolstamp"
}
need_stdlib_rt() { # the stdlib RUNTIME dll; rebuild if missing OR toolchain changed (the ref dll is an input)
	_stamp_fresh "$STDLIB_RT_DLL" "$(_toolstamp_rt)" && return 0
	info "building stdlib RUNTIME dll (missing or toolchain fingerprint changed)" >&2
	bash "$ROOT/scripts/build-stdlib-rt.sh" --emit >/dev/null 2>&1
	_toolstamp_rt > "$STDLIB_RT_DLL.toolstamp"
}

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
