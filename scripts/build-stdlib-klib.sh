#!/usr/bin/env bash
# Build the CLR frontend stdlib KLIB used by kotc's common/metadata frontend.
# This replaces the old JVM frontend jar path: kotc now resolves kotlin.* from
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
LIBCP="$(echo toolchain/kotc/build/install/kotc/lib/*.jar | tr ' ' ':')"
OUT="$ROOT/build/clr-stdlib-frontend-klib"
KLIB="$FE_KLIB"
rm -rf "$OUT"; mkdir -p "$OUT"

mapfile -t COMMON   < <(find libraries/stdlib/common/src -name '*.kt')
mapfile -t SRC      < <(find libraries/stdlib/src -name '*.kt')
mapfile -t UNSIGNED < <(find libraries/stdlib/unsigned/src -name '*.kt')

COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}")
COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"

java -cp "$LIBCP" org.jetbrains.kotlin.cli.metadata.KotlinMetadataCompiler \
	"${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" \
	-Xmetadata-klib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
	-Xmulti-platform -Xcommon-sources="$COMMON_CSV" $STDLIB_OPTIN \
	-d "$KLIB"

[[ -e "$KLIB" ]] || die "expected KLIB at $KLIB"
info "frontend klib: $KLIB ($(du -sh "$KLIB" | awk '{print $1}'))"
