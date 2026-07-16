#!/usr/bin/env bash
# Build the stdlib REFERENCE assembly (DotKt.Private.Stdlib.dll): compile the real pure-Kotlin stdlib
# (libraries/stdlib/{common,src,unsigned}/src + the clr/ actuals) in ref mode (bir2cir/ilemit
# `--build-stdlib=metadata`, no SUBSTITUTE — @Clr stays metadata) to BIR, then with --emit bir2cir -> ilemit -> retarget. The ref is
# compile-time only (bir2cir's --compile-refs, sourcing the @ClrTypeAlias/@ClrIntrinsic labels), never loaded at
# runtime — fully substituted away at app-emit; the shipping RUNTIME assembly is DotKt.Stdlib.dll
# (build-stdlib-rt.sh). The 'Private' name marks it as an internal reference face, not an external
# artifact. Inputs: libraries/stdlib sources + kotc + the bir2cir/ilemit/retarget dlls. Outputs:
# build/clr-stdlib/{bir,cir,dll} + *.err logs. NOTE: the pure-Kotlin stdlib is SELF-CONTAINED — it must
# NOT reference any runtime assembly, so the kotc step takes no --ref on purpose.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME [--emit]
  --emit       also run bir2cir + ilemit + retarget to produce DotKt.Private.Stdlib.dll
               (default: frontend + BIR only, for fast triage)
  -h, --help   this help
Exits nonzero if the frontend produced no BIR, or (with --emit) if the dll was not emitted.
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

OUT="$ROOT/build/clr-stdlib"; BIR="$OUT/bir"; CIR="$OUT/cir"; DLL="$OUT/dll"
need_kotc
rm -rf "$BIR"; mkdir -p "$BIR"

collect_stdlib_sources
stdlib_fragment_args
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xreturn-value-checker=check -XXLanguage:+UnnamedLocalVariables -Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN)

info "kotc: ${#STDLIB_COMMON[@]} common + ${#STDLIB_SRC[@]} src + ${#STDLIB_UNSIGNED[@]} unsigned + ${#STDLIB_CLR[@]} clr -> BIR (ref mode)"
# kotc exits nonzero when there are frontend errors; this script's job is to REPORT them, so tolerate it.
CLR_TYPES_METADATA="" "$KOTC" \
	"${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
	"${FLAGS[@]}" "${STDLIB_FRAGMENT_ARGS[@]}" -d "$BIR" 2>"$OUT/kotc.err" || true
bir_count="$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
echo "frontend errors: $(grep -c ': error:' "$OUT/kotc.err")   BIR files: $bir_count"
echo "--- top error kinds ---"
grep ': error:' "$OUT/kotc.err" | sed -E 's/^.*: error: //; s/'"'"'[^'"'"']*'"'"'/X/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -15 || true
(( bir_count > 0 )) || die "frontend produced no BIR (see $OUT/kotc.err)"

if (( do_emit )); then
	need_tool bir2cir; need_tool ilemit
	need_dotnet_reference_sets
	rm -rf "$CIR" "$DLL"; mkdir -p "$CIR" "$DLL"
	info "bir2cir -> CIR (ref mode)"
	dotnet "$BIR2CIR_DLL" "$CIR" --compile-refs "$FRAMEWORK_COMPILE_REFS" --build-stdlib=metadata "$BIR"/*.bir.json 2>"$OUT/bir2cir.err" || true
	echo "CIR files: $(ls "$CIR"/*.cir.json 2>/dev/null | wc -l)"
	info "ilemit(CIR) -> DotKt.Private.Stdlib.dll"
	{ dotnet "$ILEMIT_DLL" "$DLL" DotKt.Private.Stdlib --runtime-refs "" --build-stdlib=metadata "$CIR"/*.cir.json 2>"$OUT/ilemit.err" || true; } | tail -2
	grep -vE '^\s+at ' "$OUT/ilemit.err" | grep -iE 'exception|KeyNot|unresolved|no matching' | head -3 || true
	[[ -f "$DLL/DotKt.Private.Stdlib.dll" ]] || die "DotKt.Private.Stdlib.dll was not emitted (see $OUT/ilemit.err)"
	# Retarget: the emitted dll references the IMPLEMENTATION core (System.Private.CoreLib); repoint those refs at
	# the REFERENCE assemblies (+ self) so a downstream MetadataLoadContext reader — facadegen --scan-asm, ilverify —
	# can resolve its types. Self-contained (no DotKt.Runtime ref), so retarget against the BCL ref pack only.
	need_tool retarget
	info "retarget: repoint CoreLib refs (so facadegen/ilverify can read it back)"
	dotnet "$RETARGET_DLL" "$DLL/DotKt.Private.Stdlib.dll" --compile-refs "$FRAMEWORK_COMPILE_REFS" 2>&1 | tail -1
	ls -la "$DLL/DotKt.Private.Stdlib.dll"
	info "*** DotKt.Private.Stdlib.dll emitted ***"
fi
