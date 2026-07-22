#!/usr/bin/env bash
# Build the CLR frontend stdlib KLIB used by kotc's common/metadata frontend.
# This replaces the old JVM frontend JAR path: kotc now resolves kotlin.* from
# a metadata KLIB and runs FIR2IR explicitly on the common side.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME
Builds $FE_KLIB from the libraries/stdlib sources. -h for this help.
Exits nonzero if the KLIB was not produced.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

need_kotc
cd "$ROOT"
OUT="$ROOT/build/clr-stdlib-frontend-klib"
KLIB="$FE_KLIB"
rm -rf "$OUT"; mkdir -p "$OUT"

# The frontend KLIB must carry the ACTUALIZED clr fragment (libraries/stdlib/clr/), not just the
# common/expect sources: without it, every kotlin.* builtin an app resolves from the klib is an
# unactualized `expect` (no @ClrTypeAlias/@ClrIntrinsic metadata, no compiled const value) — see
# task #80. Reuses the SAME common+clr HMPP-fragment mechanism as build-stdlib-rt.sh
# (collect_stdlib_sources + stdlib_fragment_args in lib.sh): common = expect/impl multiplatform
# sources, clr = the actual CLR builtins/platform actuals that refine it.
#
# This runs kotc's OWN binary (DOTKT_BUILD_KLIB=1), NOT the stock `KotlinMetadataCompiler` CLI class:
# the stock class's HMPP configuration updater explicitly REJECTS -Xfragments during metadata
# compilation ("HMPP module structure should not be passed during metadata compilation" — verified
# empirically), and even where a stock phase *would* accept fragments (the newer pipeline-based
# MetadataKlibSerializerPhase) it hardcodes `constValueProvider = null`, so const val initializers
# never carry a compiled value into the klib. kotc's ClrMetadataKlibFir2IrPhase +
# ClrMetadataKlibSerializerPhase (toolchain/kotc/.../pipeline/ClrMetadataKlibPipeline.kt) run Fir2Ir
# first (which const-folds the whole actualized module as a side effect) and wire that real
# ConstValueProviderImpl into the serializer instead.
collect_stdlib_sources
stdlib_fragment_args

info "frontend klib sources: ${#STDLIB_COMMON[@]}+${#STDLIB_SRC[@]}+${#STDLIB_UNSIGNED[@]} common + ${#STDLIB_CLR[@]} clr"
DOTKT_BUILD_KLIB=1 "$KOTC" \
	"${STDLIB_COMMON[@]}" "${STDLIB_SRC[@]}" "${STDLIB_UNSIGNED[@]}" "${STDLIB_CLR[@]}" \
	-Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
	-Xreturn-value-checker=check -XXLanguage:+UnnamedLocalVariables \
	-Xcommon-sources="$STDLIB_COMMON_CSV" $STDLIB_OPTIN \
	"${STDLIB_FRAGMENT_ARGS[@]}" \
	-d "$KLIB" 2>"$OUT/kotc.err" || true
grep ': error:' "$OUT/kotc.err" | sed -E 's/^.*: error: //' | sort | uniq -c | sort -rn | head -10 || true

[[ -e "$KLIB" ]] || die "expected KLIB at $KLIB (see $OUT/kotc.err)"
info "frontend klib: $KLIB ($(du -sh "$KLIB" | awk '{print $1}'))"
