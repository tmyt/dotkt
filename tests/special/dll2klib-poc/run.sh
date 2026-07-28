#!/usr/bin/env bash
# End-to-end proof that a CLR reference assembly can be projected directly to a
# standard Kotlin 2.4.0 KLIB, with no facadegen JSON and no kotc declaration
# generation extension: CLR ref.dll -> .klib -> kotc -> BIR -> bir2cir -> ilemit.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-poc
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the CLR-reference-to-standard-KLIB proof of concept. -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

OUT="$ROOT/build/dll2klib-poc"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/bir" "$OUT/cir" "$OUT/il"
case "${OS:-}" in
	Windows_NT) KLIB_CP_SEP=';' ;;
	*) KLIB_CP_SEP=':' ;;
esac

need_kotc
need_fe_klib
need_stdlib_ref
need_stdlib_rt
need_dotnet_reference_sets
build_tool bir2cir
build_tool ilemit

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-poc/reference/Probe.csproj" -c Release -v:q --nologo

PROBE_REF="$ROOT/tests/special/dll2klib-poc/reference/obj/Release/net10.0/ref/Probe.dll"
PROBE_IMPL="$ROOT/tests/special/dll2klib-poc/reference/bin/Release/net10.0/Probe.dll"
PROBE_KLIB="$OUT/Probe.klib"

dotnet "$OUT/tools/dll2klib.dll" "$PROBE_REF" "$PROBE_KLIB"
dotnet "$OUT/tools/dll2klib.dll" "$PROBE_REF" "$OUT/Probe.second.klib"
cmp -s "$PROBE_KLIB" "$OUT/Probe.second.klib" || die "same MVID did not produce a deterministic KLIB"
for entry in default/manifest default/linkdata/module default/linkdata/root_package/0_.knm default/linkdata/package_Probe/0_Probe.knm; do
	unzip -Z1 "$PROBE_KLIB" | grep -qx "$entry" || die "generated KLIB is missing $entry"
done

# The only classpath metadata for Probe.Widget is the packed KLIB. In particular,
# CLR_TYPES_METADATA is absent, so the old FIR injector cannot participate.
env -u CLR_TYPES_METADATA "$KOTC" "$ROOT/tests/special/dll2klib-poc/consumer.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB" -d "$OUT/bir"

compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$PROBE_REF")"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$compile_refs" "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" Consumer --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$PROBE_IMPL")" \
	"$OUT/cir/consumer.cir.json"
cp "$STDLIB_RT_DLL" "$PROBE_IMPL" "$OUT/il/"

actual="$(dotnet "$OUT/il/Consumer.dll")"
[[ "$actual" == "100" ]] || die "generated program returned '$actual', expected '100'"
grep -q '"k": "clrInstance"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR instance member"
grep -q '"k": "clrStatic"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR static member"

info "PASS  CLR ref.dll -> standard KLIB (types, nested types, members, generics, NRT, delegates, indexers, events, extensions, operators, byref) -> kotc -> bir2cir -> ilemit -> run (100)"
