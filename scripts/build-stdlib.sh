#!/usr/bin/env bash
# UNIFIED stdlib build (#66): ONE kotc frontend run producing a SHARED, substitute-independent BIR, then BOTH the
# REFERENCE (DotKt.Private.Stdlib.dll) and RUNTIME (DotKt.Stdlib.dll) assemblies emitted from that same BIR by bir2cir
# + ilemit. Since #66 kotc no longer reads DOTKT_STDLIB_SUBSTITUTE / DOTKT_STRIP_METADATA — the ref/rt divergence (BCL
# substitution, the kotlin.Comparable-bound + `in`-variance drops, the metadata strip, body-squash) is ENTIRELY
# bir2cir's + ilemit's — so the frontend runs exactly once and its BIR is cacheable/shared. This supersedes the
# separate build-stdlib-ref.sh + build-stdlib-rt.sh (each of which ran its own kotc); those remain until the gate
# confirms this unified path. Outputs to the CANONICAL locations (STDLIB_REF_DLL / STDLIB_RT_DLL) so all downstream
# consumers (dotkt.sh, verify-*.sh) find them unchanged. See docs/architecture.md.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME [--emit]
  --emit       also run bir2cir + ilemit (+ retarget) to produce BOTH DotKt.Private.Stdlib.dll and DotKt.Stdlib.dll
               (default: ONE kotc frontend run producing the shared BIR only, for fast triage)
  -h, --help   this help
Exits nonzero if the frontend produced no BIR, or (with --emit) if either dll was not emitted.
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

SHARED="$ROOT/build/clr-stdlib-shared"; BIR="$SHARED/bir"
REF_OUT="$ROOT/build/clr-stdlib";    REF_CIR="$REF_OUT/cir"; REF_DLL="$REF_OUT/dll"
RT_OUT="$ROOT/build/clr-stdlib-rt";  RT_CIR="$RT_OUT/cir";   RT_DLL="$RT_OUT/dll"
need_kotc
rm -rf "$BIR"; mkdir -p "$BIR"

collect_stdlib_sources
stdlib_fragment_args
FLAGS=(-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters -Xreturn-value-checker=check -XXLanguage:+UnnamedLocalVariables -Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN)

# ONE frontend run — substitute-independent (kotc ignores SUBSTITUTE/STRIP since #66). This single BIR feeds BOTH the
# ref and rt emits below.
info "kotc (ONE run): ${#STDLIB_COMMON[@]} common + ${#STDLIB_SRC[@]} src + ${#STDLIB_UNSIGNED[@]} unsigned + ${#STDLIB_CLR[@]} clr -> SHARED BIR"
# kotc exits nonzero when there are frontend errors; this script's job is to REPORT them, so tolerate it.
"$KOTC" \
	"${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
	"${FLAGS[@]}" "${STDLIB_FRAGMENT_ARGS[@]}" -d "$BIR" 2>"$SHARED/kotc.err" || true
bir_count="$(ls "$BIR"/*.bir.json 2>/dev/null | wc -l)"
echo "frontend errors: $(grep -c ': error:' "$SHARED/kotc.err")   BIR files: $bir_count"
echo "--- top error kinds ---"
grep ': error:' "$SHARED/kotc.err" | sed -E 's/^.*: error: //; s/'"'"'[^'"'"']*'"'"'/X/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -15 || true
(( bir_count > 0 )) || die "frontend produced no BIR (see $SHARED/kotc.err)"

if (( do_emit )); then
	need_tool bir2cir; need_tool ilemit
	need_dotnet_reference_sets

	# --- REFERENCE assembly (DotKt.Private.Stdlib.dll): bir2cir refBuild (kotlin.* verbatim + @Clr metadata, bodies
	#     squashed to `throw`) -> ilemit -> retarget. Self-contained (no runtime ref).
	rm -rf "$REF_CIR" "$REF_DLL"; mkdir -p "$REF_CIR" "$REF_DLL"
	info "REF: bir2cir -> CIR"
	dotnet "$BIR2CIR_DLL" "$REF_CIR" --compile-refs "$FRAMEWORK_COMPILE_REFS" --build-stdlib=metadata "$BIR"/*.bir.json 2>"$REF_OUT/bir2cir.err" || true
	echo "REF CIR files: $(ls "$REF_CIR"/*.cir.json 2>/dev/null | wc -l)"
	info "REF: ilemit -> DotKt.Private.Stdlib.dll"
	{ dotnet "$ILEMIT_DLL" "$REF_DLL" DotKt.Private.Stdlib --runtime-refs "" --build-stdlib=metadata "$REF_CIR"/*.cir.json 2>"$REF_OUT/ilemit.err" || true; } | tail -2
	grep -vE '^\s+at ' "$REF_OUT/ilemit.err" | grep -iE 'exception|KeyNot|unresolved|no matching' | head -3 || true
	[[ -f "$REF_DLL/DotKt.Private.Stdlib.dll" ]] || die "DotKt.Private.Stdlib.dll was not emitted (see $REF_OUT/ilemit.err)"
	need_tool retarget
	info "REF: retarget (so metadata readers/ILVerify can read it back)"
	dotnet "$RETARGET_DLL" "$REF_DLL/DotKt.Private.Stdlib.dll" --compile-refs "$FRAMEWORK_COMPILE_REFS" 2>&1 | tail -1
	info "*** DotKt.Private.Stdlib.dll emitted ***"

	# --- RUNTIME assembly (DotKt.Stdlib.dll): bir2cir substitute (@Clr ACTIVE — BCL substitution, Comparable-bound +
	#     `in`-variance drops), reading the JUST-BUILT ref.dll for the @ClrTypeAlias/@ClrIntrinsic labels -> ilemit
	#     (metadata-stripped). Same SHARED BIR as the ref emit above.
	rm -rf "$RT_CIR" "$RT_DLL"; mkdir -p "$RT_CIR" "$RT_DLL"
	rt_compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
	info "RT: bir2cir (substitute) -> CIR"
	{ dotnet "$BIR2CIR_DLL" "$RT_CIR" --compile-refs "$rt_compile_refs" --build-stdlib=runtime "$BIR"/*.bir.json 2>"$RT_OUT/bir2cir.err" || true; } | tail -1
	echo "RT CIR files: $(ls "$RT_CIR"/*.cir.json 2>/dev/null | wc -l)"
	info "RT: ilemit (substitute) -> DotKt.Stdlib.dll"
	{ dotnet "$ILEMIT_DLL" "$RT_DLL" DotKt.Stdlib --runtime-refs "" --build-stdlib=runtime "$RT_CIR"/*.cir.json 2>"$RT_OUT/ilemit.err" || true; } | tail -2
	grep -vE '^\s+at ' "$RT_OUT/ilemit.err" | grep -iE 'exception|error|unresolved|no matching|not found|cannot' | head -3 || true
	[[ -f "$RT_DLL/DotKt.Stdlib.dll" ]] || die "DotKt.Stdlib.dll was not emitted (see $RT_OUT/ilemit.err)"
	info "RT: retarget (so ordinary CLR tooling can consume the runtime assembly)"
	dotnet "$RETARGET_DLL" "$RT_DLL/DotKt.Stdlib.dll" --compile-refs "$FRAMEWORK_COMPILE_REFS" >/dev/null
	info "*** DotKt.Stdlib.dll emitted ***"
	info "*** unified stdlib build complete (ONE kotc run -> ref + rt) ***"
fi
