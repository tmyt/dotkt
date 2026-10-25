#!/usr/bin/env bash
# Build the stdlib RUNTIME assembly (DotKt.Stdlib.dll — the ref/runtime split's impl side). Same sources
# as build-stdlib-ref.sh, but in SUBSTITUTE mode (bir2cir/ilemit `--build-stdlib=runtime`): the @Clr bindings
# are ACTIVE, so @Clr-bound TYPES resolve to the BCL and are NOT emitted (no clash with the ref's pure-Kotlin
# shapes) while the stdlib FUNCTIONS (listOf/map/filter/asList) are emitted with substituted signatures.
# bir2cir reads the REFERENCE assembly (build-stdlib-ref.sh — must exist first) for the @ClrTypeAlias/
# @ClrIntrinsic call-substitution labels. Inputs: libraries/stdlib sources + kotc + bir2cir/ilemit dlls +
# the ref dll. Outputs: build/clr-stdlib-rt/{bir,cir,dll} + *.err logs.
# See docs/architecture.md.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME [--emit]
  --emit       also run bir2cir + ilemit to produce DotKt.Stdlib.dll
               (default: frontend + BIR only, for fast triage)
  -h, --help   this help
Exits nonzero if any compiler stage fails or its required output is missing.
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
stdlib_reference_args
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xreturn-value-checker=check -XXLanguage:+UnnamedLocalVariables -Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN)

info "SUBSTITUTE-mode kotc: ${#STDLIB_COMMON[@]}+${#STDLIB_SRC[@]}+${#STDLIB_UNSIGNED[@]}+${#STDLIB_CLR[@]} stdlib files -> BIR (@Clr ACTIVE)"
run_stdlib_stage "$OUT/kotc.err" "$KOTC" \
	"${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
	"${FLAGS[@]}" "${STDLIB_FRAGMENT_ARGS[@]}" "${STDLIB_REFERENCE_ARGS[@]}" -d "$BIR"
bir_count="$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
echo "BIR files: $bir_count"
(( bir_count > 0 )) || die "frontend produced no BIR (see $OUT/kotc.err)"

if (( do_emit )); then
	need_tool bir2cir; need_tool ilemit
	need_dotnet_reference_sets
	info "bir2cir (substitute) -> CIR"
	# bir2cir reads the REFERENCE assembly for the @ClrTypeAlias/@ClrIntrinsic call-substitution labels
	# (member calls on CLR-bound owners -> plain BCL calls). Must exist — build the ref first.
	[[ -f "$STDLIB_REF_DLL" ]] || die "reference stdlib dll not found at $STDLIB_REF_DLL — run 'build-stdlib-ref.sh --emit' first (the runtime build reads it for the @ClrTypeAlias/@ClrIntrinsic labels)"
	compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
	run_stdlib_stage "$OUT/bir2cir.err" dotnet "$BIR2CIR_DLL" "$CIR" --compile-refs "$compile_refs" --build-stdlib=runtime \
		--stdlib-bindings "$STDLIB_BINDINGS" "$BIR"/*.bir.json | tail -1
	info "ilemit (substitute) -> DotKt.Stdlib.dll"
	run_stdlib_stage "$OUT/ilemit.err" dotnet "$ILEMIT_DLL" "$DLL" DotKt.Stdlib --compile-refs "$FRAMEWORK_COMPILE_REFS" --runtime-refs "" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" --build-stdlib=runtime "$CIR"/*.cir.json | tail -2
	# Diagnostic filtering is informational; process status above determines stage success.
	grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|error|unresolved|no matching|not found|cannot' | head -3 || true
	[[ -f "$DLL/DotKt.Stdlib.dll" ]] || die "DotKt.Stdlib.dll was not emitted (see $OUT/ilemit.err)"
	info "*** DotKt.Stdlib.dll emitted ***"
fi
