# scripts/lib.sh — shared conventions for build helpers and shell-based test suites. Source it, never execute it:
#
#   source "$ROOT/scripts/lib.sh"
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
DLL2KLIB_DLL="$ROOT/build/dll2klib-bin/dll2klib.dll"
FE_KLIB="$ROOT/build/clr-stdlib-frontend-klib/kotlin-stdlib-clr-frontend.klib"
STDLIB_REF_DLL="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
STDLIB_RT_DLL="$ROOT/build/clr-stdlib-rt/dll/DotKt.Stdlib.dll"
DOTKT_TFM="${DOTKT_TFM:-net10.0}"
DOTKT_TARGET_FRAMEWORK_MONIKER="${DOTKT_TARGET_FRAMEWORK_MONIKER:-.NETCoreApp,Version=v10.0}"
DOTKT_RUNTIME_FRAMEWORK_NAME="${DOTKT_RUNTIME_FRAMEWORK_NAME:-Microsoft.NETCore.App}"
DOTKT_RUNTIME_FRAMEWORK_VERSION="${DOTKT_RUNTIME_FRAMEWORK_VERSION:-10.0.0}"

# The direct-run scripts do not receive MSBuild's @(ReferencePath), so construct the equivalent framework compile
# set from the installed Microsoft.NETCore.App.Ref targeting pack.  This is an authoritative pack enumeration, not
# a probe of an arbitrary referenced assembly's neighbouring directory.
need_dotnet_reference_sets() {
	[[ -n "${DOTNET_REFPACK_DIR:-}" && -n "${FRAMEWORK_COMPILE_REFS:-}" ]] && return 0
	local sdk_root dotnet_root
	sdk_root="$(dotnet --list-sdks | sed -n 's/^.*\[\(.*\)\]$/\1/p' | tail -1)"
	[[ -n "$sdk_root" ]] || die "could not locate the dotnet SDK root"
	dotnet_root="$(dirname "$sdk_root")"
	DOTNET_REFPACK_DIR="$(find "$dotnet_root/packs/Microsoft.NETCore.App.Ref" -type d -path "*/ref/$DOTKT_TFM" 2>/dev/null | sort -V | tail -1)"
	[[ -d "$DOTNET_REFPACK_DIR" ]] || die "Microsoft.NETCore.App.Ref for $DOTKT_TFM is not installed under $dotnet_root/packs"
	mapfile -t FRAMEWORK_COMPILE_REF_PATHS < <(find "$DOTNET_REFPACK_DIR" -maxdepth 1 -type f -name '*.dll' | LC_ALL=C sort)
	FRAMEWORK_COMPILE_REFS="$(refset_join "${FRAMEWORK_COMPILE_REF_PATHS[@]}")"

	local major="${DOTKT_TFM#net}"; major="${major%%.*}"
	local runtime_line runtime_base runtime_ver
	runtime_line="$(dotnet --list-runtimes | awk -v m="$major." '$1=="Microsoft.NETCore.App" && index($2,m)==1' | sort -V | tail -1)"
	runtime_ver="$(awk '{print $2}' <<<"$runtime_line")"
	runtime_base="$(sed -n 's/^.*\[\(.*\)\]$/\1/p' <<<"$runtime_line")"
	DOTNET_RUNTIME_DIR="$runtime_base/$runtime_ver"
	[[ -d "$DOTNET_RUNTIME_DIR" ]] || die "Microsoft.NETCore.App runtime for $DOTKT_TFM is not installed"
}

refset_join() { # <path-or-semicolon-set>... -> one normalized semicolon list
	local result="" part
	for part in "$@"; do
		[[ -n "$part" ]] || continue
		part="${part#;}"; part="${part%;}"
		[[ -n "$part" ]] || continue
		result+="${result:+;}$part"
	done
	printf '%s' "$result"
}

# Direct shell drivers own executable scaffolding explicitly. MSBuild generates its own runtimeconfig from the
# project TFM; ilemit emits only the assembly and never infers a target from its host runtime.
write_runtimeconfig() { # <output-dir> <assembly-name>
	local output_dir="$1" assembly_name="$2"
	printf '{"runtimeOptions":{"tfm":"%s","framework":{"name":"%s","version":"%s"}}}\n' \
		"$DOTKT_TFM" "$DOTKT_RUNTIME_FRAMEWORK_NAME" "$DOTKT_RUNTIME_FRAMEWORK_VERSION" \
		> "$output_dir/$assembly_name.runtimeconfig.json"
}

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
#   FIXED     <prefix>:<name> — fixed; remove it from the xfail list   (stale baseline; reddens the gate)
# NEW-FAIL / FIXED names are appended to the global arrays below. A gate is clean only when BOTH are empty:
# an expected failure that stopped failing is a baseline change which must prune its stale entry in the same PR.
declare -a XFAIL_NEW=() XFAIL_FIXED=()
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
			XFAIL_FIXED+=("$_pfx:$n")
		fi
	done
}
xfail_gate_is_clean() { (( ${#XFAIL_NEW[@]} == 0 && ${#XFAIL_FIXED[@]} == 0 )); }

# --- lazy builders (loud when they fire) -----------------------------------------------------------
need_kotc() {
	[[ -x "$KOTC" ]] || { info "building kotc (gradlew :kotc:installDist)" >&2; (cd "$ROOT" && ./gradlew -q :kotc:installDist); }
}
need_tool() { # <name> — ensure build/<name>-bin/<name>.dll exists; lazy
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
# '<artifact>.toolstamp' and rebuild on MISMATCH, not just absence. The hash covers CONTENT+PATH, never mtime:
# installDist/dotnet build may replace an unchanged output and refresh its mtime on every pack invocation.
# An mtime stamp therefore invalidates itself after pack rebuilds the tools (#223), while a content stamp keeps
# unchanged packs idempotent and still rejects every actual tool/source/reference-pack content change.
KOTC_INSTALL_DIR="$ROOT/toolchain/kotc/build/install/kotc"
STDLIB_SRC_DIR="$ROOT/libraries/stdlib"
STDLIB_BUILD_LIB="$ROOT/scripts/lib.sh"

# _toolstamp <path>... — fingerprint the given input files/dirs. Missing paths contribute nothing (a build
# that truly lacks an input fails loudly on its own). Sort the NUL-delimited absolute paths, hash each file,
# then hash that ordered manifest. `sha256sum` records both content digest and path, so additions/removals and
# path changes invalidate the stamp without treating a metadata-only touch as a compiler change.
_toolstamp() {
	find "$@" -type f -print0 2>/dev/null \
		| LC_ALL=C sort -z \
		| xargs -0 -r sha256sum \
		| sha256sum \
		| awk '{print $1}'
}
# Per-artifact input sets. klib: kotc + stdlib sources (a klib has no IL -> ilemit/bir2cir are irrelevant to
# its bytes). ref: kotc + bir2cir + ilemit + targeting pack + sources. rt: the same plus the REF dll it
# consumes through bir2cir's compile-reference set.
_toolstamp_klib() { _toolstamp "$KOTC_INSTALL_DIR" "$STDLIB_SRC_DIR" "$STDLIB_BUILD_LIB" "$ROOT/scripts/build-stdlib-klib.sh"; }
_toolstamp_ref()  { need_dotnet_reference_sets; _toolstamp "$KOTC_INSTALL_DIR" "$BIR2CIR_DLL" "$ILEMIT_DLL" "$DOTNET_REFPACK_DIR" "$STDLIB_SRC_DIR" "$STDLIB_BUILD_LIB" "$ROOT/scripts/build-stdlib-ref.sh"; }
_toolstamp_rt()   { need_dotnet_reference_sets; _toolstamp "$KOTC_INSTALL_DIR" "$BIR2CIR_DLL" "$ILEMIT_DLL" "$STDLIB_REF_DLL" "$DOTNET_REFPACK_DIR" "$STDLIB_SRC_DIR" "$STDLIB_BUILD_LIB" "$ROOT/scripts/build-stdlib-rt.sh"; }
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
