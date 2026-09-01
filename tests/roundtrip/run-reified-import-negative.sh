#!/usr/bin/env bash
# The producer's semantic `reified` marker must survive DLL -> KLIB independently of its physical nullable-witness
# parameter. Compile a fresh consumer against that projected KLIB and require the Kotlin frontend's ordinary rule;
# inspecting the protobuf bit alone would not prove that consumers actually enforce it.
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
source_file="$ROOT/tests/roundtrip/reified-import-negative/NonReifiedArgument.kt"
if "$KOTC" "$source_file" -no-stdlib \
	-classpath "$FE_KLIB$klib_cp_sep$producer_klib" -d "$work/bir" >"$work/compiler.log" 2>&1; then
	die "frontend accepted a non-reified type argument for an imported reified declaration"
fi
expected="cannot use 'U' as reified type parameter. Use a class instead."
grep -Fq "$expected" "$work/compiler.log" \
	|| die "imported reified declaration produced the wrong diagnostic: $(cat "$work/compiler.log")"

echo "imported semantic reified marker enforces the frontend type-argument rule"
