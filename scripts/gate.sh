#!/usr/bin/env bash
# gate.sh — the CHANGE-AWARE gate wrapper.
#
# The canonical suites (scripts/verify-*.sh) each rebuild + gate a WHOLE stage from scratch; running the
# complete set (make verify-core) is ~45 min and rebuilds the stdlib every time regardless of what
# changed. This wrapper reads the set of CHANGED paths and runs the MINIMAL CORRECT rebuild + suite
# subset for exactly those changes. It does NOT reimplement any suite — it selects which of the existing
# scripts/verify-*.sh to invoke (verbatim) and whether to force a clean stdlib rebuild first.
#
# It is a SELECTOR, not a new gate: `gate.sh --full` runs the complete verify-core set with a clean
# stdlib rebuild and is the AUTHORITATIVE pre-merge / release gate the coordinator uses for integration.
# The change-aware subset is a fast DEV convenience; when in doubt it falls back to --full (CONSERVATIVE:
# a wrong "skip" that hides a regression is far worse than a slow gate — see MEMORY
# build-cache-masks-stdlib-regressions).
#
# CHANGE SOURCE (default): the union of committed diff vs main (git diff main...HEAD), staged, unstaged,
# and untracked paths. Override by passing explicit paths as arguments (e.g. `gate.sh toolchain/ilemit`).
#
# SELECTION RULES (each changed path contributes; the union is run; any UNMATCHED path forces --full):
#   *.md / docs/** / CHANGELOG*        -> nothing  (schema doc -> verify-schema only)
#   scripts/verify-<x>.sh (+ .py)      -> just that suite
#   scripts/gate.sh                    -> nothing (this wrapper itself, no pipeline effect)
#   scripts/{lib,build-stdlib*,dotkt,  -> FULL   (shared build machinery — affects every stage)
#     gen-*,pack-nuget}.sh
#   toolchain/facadegen/**             -> facadegen is rebuilt by the suites; run verify-roundtrip +
#                                          verify-ktproj + verify-il (facadegen metadata feeds all three)
#   toolchain/bir2cir/** | ilemit/**   -> stdlib EMIT (clean) + verify-il + verify-differential +
#                                          verify-schema + verify-sanity   (kotc unchanged: no installDist cost)
#   toolchain/bir-common/**            -> FULL   (TypeNode/IrSanity are <Compile Link/>-shared into every tool)
#   toolchain/retarget/**              -> FULL   (a stdlib-bake input + BCL-repoint used by roundtrip/ktproj)
#   toolchain/kotc/** | libraries/stdlib/**  -> FULL + clean stdlib rebuild
#   cases/ktproj*/**                   -> verify-ktproj
#   cases/**                           -> verify-il + verify-differential
#   anything else                      -> FULL
#
# CLEAN STDLIB REBUILD (rm -rf build/clr-stdlib*) happens iff a stdlib-BAKING axis changed
# (kotc / bir2cir / ilemit / retarget / stdlib sources / --full) — the exact axes the cache-masking
# landmine names. On every other selection the stdlib artifacts are reused (lib.sh need_* + the #13
# toolstamp still rebuild them if their fingerprint drifted, so reuse is safe).
source "$(dirname "$0")/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME [--full] [--release] [--dry-run] [PATH...]
Change-aware wrapper over the scripts/verify-*.sh suites: runs the minimal correct rebuild + suite
subset for the changed paths (default: git-detected). Prints exactly what it chose and WHY.

  --full        run the complete verify-core set with a clean stdlib rebuild (authoritative pre-merge)
  --release     --full plus the packaged-SDK gate (matches \`make verify\`)
  -n, --dry-run print the selected rebuilds + suites and exit WITHOUT running anything
  PATH...       classify these explicit paths instead of the git-detected change set
  -h, --help    this help
EOF
}

FULL=0; RELEASE=0; DRYRUN=0; EXPLICIT_PATHS=()
while (( $# )); do
	case "$1" in
		--full) FULL=1 ;;
		--release) FULL=1; RELEASE=1 ;;
		-n|--dry-run) DRYRUN=1 ;;
		-h|--help) usage; exit 0 ;;
		-*) usage_error "unknown argument '$1'" ;;
		*) EXPLICIT_PATHS+=("$1") ;;
	esac
	shift
done

# ---- change set -----------------------------------------------------------------------------------
collect_changes() {
	if (( ${#EXPLICIT_PATHS[@]} )); then
		printf '%s\n' "${EXPLICIT_PATHS[@]}"
		return
	fi
	local base
	base="$(git -C "$ROOT" merge-base main HEAD 2>/dev/null || echo main)"
	{
		git -C "$ROOT" diff --name-only "$base"...HEAD 2>/dev/null || true   # committed since main
		git -C "$ROOT" diff --name-only 2>/dev/null || true                  # unstaged
		git -C "$ROOT" diff --name-only --cached 2>/dev/null || true         # staged
		git -C "$ROOT" ls-files --others --exclude-standard 2>/dev/null || true  # untracked
	} | sed '/^$/d' | sort -u
}

# ---- selection state ------------------------------------------------------------------------------
declare -A WANT=()          # suite -> 1 (il schema sanity ktproj roundtrip differential widedelegates packagedsdk)
declare -a REASONS=()       # human-readable "path -> decision" lines
CLEAN=0                     # force a clean stdlib rebuild
NEED_FULL=0                 # an unmatched/broad path forces the complete set
want() { WANT["$1"]=1; }
reason() { REASONS+=("$1"); }

classify() { # <path>
	local p="$1"
	case "$p" in
		# ---- docs / changelog -------------------------------------------------------------------
		docs/bir-cir-spec.md|docs/bir-cir.schema.json|docs/design-fir-bir-cir-il.md)
			want schema; reason "$p -> verify-schema (BIR/CIR schema doc)" ;;
		*.md|docs/*|CHANGELOG*)
			reason "$p -> (no gate: docs)" ;;
		# ---- scripts ----------------------------------------------------------------------------
		scripts/gate.sh)
			reason "$p -> (no gate: the wrapper itself, no pipeline effect)" ;;
		scripts/verify-il.sh)          want il;            reason "$p -> verify-il" ;;
		scripts/verify-schema.sh|scripts/verify-schema.py)    want schema;   reason "$p -> verify-schema" ;;
		scripts/verify-sanity.sh|scripts/verify-sanity.py)    want sanity;   reason "$p -> verify-sanity" ;;
		scripts/verify-ktproj.sh)      want ktproj;        reason "$p -> verify-ktproj" ;;
		scripts/verify-roundtrip.sh)   want roundtrip;     reason "$p -> verify-roundtrip" ;;
		scripts/verify-differential.sh) want differential; reason "$p -> verify-differential" ;;
		scripts/verify-wide-delegates.sh) want widedelegates; reason "$p -> verify-wide-delegates" ;;
		scripts/verify-packaged-sdk.sh) want packagedsdk;  reason "$p -> verify-packaged-sdk" ;;
		scripts/lib.sh|scripts/build-stdlib*.sh|scripts/dotkt.sh|scripts/gen-*|scripts/pack-nuget.sh|scripts/hooks/*)
			NEED_FULL=1; reason "$p -> FULL (shared build machinery)" ;;
		# ---- toolchain --------------------------------------------------------------------------
		toolchain/facadegen/*)
			want roundtrip; want ktproj; want il
			reason "$p -> facadegen: verify-roundtrip + verify-ktproj + verify-il (metadata feeds all three)" ;;
		toolchain/bir2cir/*|toolchain/ilemit/*)
			want il; want differential; want schema; want sanity; CLEAN=1
			reason "$p -> bir2cir/ilemit: clean stdlib emit + verify-il + verify-differential + verify-schema + verify-sanity" ;;
		toolchain/bir-common/*)
			NEED_FULL=1; CLEAN=1; reason "$p -> FULL (bir-common is <Compile Link/>-shared into every tool)" ;;
		toolchain/retarget/*)
			NEED_FULL=1; CLEAN=1; reason "$p -> FULL (retarget is a stdlib-bake input + BCL-repoint for roundtrip/ktproj)" ;;
		toolchain/kotc/*)
			NEED_FULL=1; CLEAN=1; reason "$p -> FULL + clean stdlib (kotc frontend changed)" ;;
		libraries/stdlib/*)
			NEED_FULL=1; CLEAN=1; reason "$p -> FULL + clean stdlib (stdlib source changed)" ;;
		# ---- cases ------------------------------------------------------------------------------
		cases/ktproj*/*|cases/ktproj*)
			want ktproj; reason "$p -> verify-ktproj (ktproj case)" ;;
		cases/*)
			want il; want differential; reason "$p -> verify-il + verify-differential (sample case)" ;;
		# ---- anything else ----------------------------------------------------------------------
		*)
			NEED_FULL=1; reason "$p -> FULL (unrecognized path: conservative fallback)" ;;
	esac
}

# ---- suite runners (invoke the REAL scripts verbatim) ---------------------------------------------
declare -a RUN_ORDER=(il schema sanity ktproj roundtrip differential widedelegates packagedsdk)
declare -A SUITE_SCRIPT=(
	[il]=verify-il.sh [schema]=verify-schema.sh [sanity]=verify-sanity.sh
	[ktproj]=verify-ktproj.sh [roundtrip]=verify-roundtrip.sh [differential]=verify-differential.sh
	[widedelegates]=verify-wide-delegates.sh [packagedsdk]=verify-packaged-sdk.sh
)

FULL_SUITES=(il schema sanity ktproj roundtrip differential widedelegates)

# ---- compute the plan -----------------------------------------------------------------------------
mapfile -t CHANGES < <(collect_changes)

if (( FULL )); then
	CLEAN=1
	for s in "${FULL_SUITES[@]}"; do want "$s"; done
	(( RELEASE )) && want packagedsdk
	reason "--full: complete verify-core set + clean stdlib rebuild"
	(( RELEASE )) && reason "--release: + verify-packaged-sdk"
elif (( ${#CHANGES[@]} == 0 )); then
	reason "(no changes detected vs main and no explicit paths given)"
else
	for c in "${CHANGES[@]}"; do classify "$c"; done
	if (( NEED_FULL )); then
		CLEAN=1   # an escalated FULL is the authoritative path: clean-rebuild the stdlib too (defeat cache-masking)
		for s in "${FULL_SUITES[@]}"; do want "$s"; done
		reason "==> a broad/unmatched path was seen: escalated to the FULL verify-core set + clean stdlib rebuild"
	fi
fi

# ---- print the plan -------------------------------------------------------------------------------
selected=()
for s in "${RUN_ORDER[@]}"; do [[ -v WANT[$s] ]] && selected+=("$s"); done

echo "== gate.sh plan =="
if (( ${#CHANGES[@]} )); then
	echo "changed paths (${#CHANGES[@]}):"
	printf '  %s\n' "${CHANGES[@]}"
else
	echo "changed paths: (none)"
fi
echo "decisions:"
printf '  %s\n' "${REASONS[@]}"
echo "clean stdlib rebuild: $( ((CLEAN)) && echo YES || echo no )"
if (( ${#selected[@]} )); then
	echo "suites to run: ${selected[*]}"
else
	echo "suites to run: (none)"
fi
echo "=================="

if (( DRYRUN )); then
	echo "gate.sh: --dry-run, not executing."
	exit 0
fi

# ---- execute --------------------------------------------------------------------------------------
if (( CLEAN )); then
	echo "gate.sh: clean stdlib rebuild — removing build/clr-stdlib{,-rt,-frontend-klib}"
	rm -rf "$ROOT/build/clr-stdlib" "$ROOT/build/clr-stdlib-rt" "$ROOT/build/clr-stdlib-frontend-klib"
fi

if (( ${#selected[@]} == 0 )); then
	echo "gate.sh: nothing to run for this change set — OK."
	exit 0
fi

rc=0; FAILED=(); PASSED=()
for s in "${selected[@]}"; do
	script="$ROOT/scripts/${SUITE_SCRIPT[$s]}"
	echo
	echo "########## gate.sh: running ${SUITE_SCRIPT[$s]} ##########"
	if bash "$script"; then
		PASSED+=("$s")
	else
		FAILED+=("$s"); rc=1
	fi
done

echo
echo "== gate.sh summary =="
echo "passed: ${PASSED[*]:-(none)}"
echo "failed: ${FAILED[*]:-(none)}"
(( rc == 0 )) && echo "gate.sh: GREEN" || echo "gate.sh: RED"
exit $rc
