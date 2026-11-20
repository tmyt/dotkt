#!/usr/bin/env bash
# Check frontend restrictions on imported declarations in a single compiler invocation.
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)/scripts/lib.sh"

need_kotc
need_fe_klib

producer_klib="${1:-$ROOT/tests/roundtrip/consumer/obj/Debug/net10.0/klib/RoundtripProducer.klib}"
[[ -f "$producer_klib" ]] || die "roundtrip producer KLIB is missing: $producer_klib"

case "${OS:-}" in
	Windows_NT) klib_cp_sep=';' ;;
	*) klib_cp_sep=':' ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bir"
if "$KOTC" "$ROOT"/tests/roundtrip/import-negative/*.kt -no-stdlib \
	-classpath "$FE_KLIB$klib_cp_sep$producer_klib" -d "$work/bir" >"$work/compiler.log" 2>&1; then
	die "frontend accepted prohibited use of imported declarations"
fi
expected="cannot use 'U' as reified type parameter. Use a class instead."
grep -Fq "$expected" "$work/compiler.log" \
	|| die "imported reified declaration produced the wrong diagnostic: $(cat "$work/compiler.log")"

grep -F "ExcludedClass.kt:" "$work/compiler.log" | grep -Fq "this annotation is not applicable to target 'class'" \
	|| die "expected imported @Target to exclude class declarations: $(cat "$work/compiler.log")"

grep -F "MissingMarkerOptIn.kt:" "$work/compiler.log" | grep -Fiq "preview API" \
	|| die "expected the imported marker's error message: $(cat "$work/compiler.log")"

echo "imported Kotlin reified, annotation target, and opt-in restrictions enforced"
