#!/usr/bin/env bash
# Build the stdlib RUNTIME assembly (DotKt.Stdlib.dll — the ref/runtime split's impl side). Same sources
# as build-stdlib-ref.sh, but in SUBSTITUTE mode (bir2cir/ilemit `--build-stdlib=runtime`): the @Clr bindings
# are ACTIVE, so @Clr-bound TYPES resolve to the BCL and are NOT emitted (no clash with the ref's pure-Kotlin
# shapes) while the stdlib FUNCTIONS (listOf/map/filter/asList) are emitted with substituted signatures.
# bir2cir reads the REFERENCE assembly (build-stdlib-ref.sh — must exist first) for the @ClrTypeAlias/
# @ClrIntrinsic call-substitution labels. Inputs: libraries/stdlib sources + kotc + bir2cir/ilemit dlls +
# the ref dll. Outputs: build/clr-stdlib-rt/{bir,cir,dll} + *.err logs.
# See docs/design-clr-stdlib-ref-runtime-split.md "Runtime-build architecture".
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME [--emit]
  --emit       also run bir2cir + ilemit to produce DotKt.Stdlib.dll
               (default: frontend + BIR only, for fast triage)
  -h, --help   this help
Exits 0 on success; nonzero if the frontend produced no BIR or (with --emit) the dll was not emitted.
(The old version ended with an error-grep that exited 1 exactly when the build was CLEAN, so callers
needed a compensating '|| true' — that footgun is gone.)
EOF
}

do_emit=0
while (( $# )); do
	case "$1" in
		--emit) do_emit=1; shift ;;
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

OUT="$ROOT/build/clr-stdlib-rt"; BIR="$OUT/bir"; CIR="$OUT/cir"; DLL="$OUT/dll"
need_kotc
rm -rf "$BIR" "$CIR" "$DLL"; mkdir -p "$BIR" "$CIR" "$DLL"

collect_stdlib_sources
stdlib_fragment_args
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN)

info "SUBSTITUTE-mode kotc: ${#STDLIB_COMMON[@]}+${#STDLIB_SRC[@]}+${#STDLIB_UNSIGNED[@]}+${#STDLIB_CLR[@]} stdlib files -> BIR (@Clr ACTIVE)"
# kotc exits nonzero when there are frontend errors; this script's job is to REPORT them, so tolerate it.
DOTKT_STDLIB_COMPILE=1 CLR_TYPES_METADATA="" "$KOTC" \
	"${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
	"${FLAGS[@]}" "${STDLIB_FRAGMENT_ARGS[@]}" -d "$BIR" 2>"$OUT/kotc.err" || true
bir_count="$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
echo "frontend errors: $(grep -c ': error:' "$OUT/kotc.err")   BIR files: $bir_count"
grep ': error:' "$OUT/kotc.err" | sed -E 's/^.*: error: //; s/'"'"'[^'"'"']*'"'"'/X/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -10 || true
(( bir_count > 0 )) || die "frontend produced no BIR (see $OUT/kotc.err)"

if (( do_emit )); then
	need_tool bir2cir; need_tool ilemit
	info "bir2cir (substitute) -> CIR"
	# bir2cir reads the REFERENCE assembly for the @ClrTypeAlias/@ClrIntrinsic call-substitution labels
	# (member calls on CLR-bound owners -> plain BCL calls). Must exist — build the ref first.
	refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	{ dotnet "$BIR2CIR_DLL" "$CIR" "${refarg[@]}" --build-stdlib=runtime "$BIR"/*.bir.json 2>"$OUT/bir2cir.err" || true; } | tail -1
	info "ilemit (substitute) -> DotKt.Stdlib.dll"
	{ dotnet "$ILEMIT_DLL" "$DLL" DotKt.Stdlib --build-stdlib=runtime "$CIR"/*.cir.json 2>"$OUT/ilemit.err" || true; } | tail -2
	# Report (but do not fail on) interesting emitter noise; the REAL success signal is the dll below.
	grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|error|unresolved|no matching|not found|cannot' | head -3 || true
	[[ -f "$DLL/DotKt.Stdlib.dll" ]] || die "DotKt.Stdlib.dll was not emitted (see $OUT/ilemit.err)"
	info "*** DotKt.Stdlib.dll emitted ***"
fi
