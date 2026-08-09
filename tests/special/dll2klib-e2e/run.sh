#!/usr/bin/env bash
# End-to-end regression test for projecting a CLR reference assembly directly to a
# standard Kotlin 2.4.0 KLIB, with no dll2klib JSON and no kotc declaration
# generation extension: CLR ref.dll -> .klib -> kotc -> BIR -> bir2cir -> ilemit.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=dll2klib-e2e
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the CLR-reference-to-standard-KLIB regression test. -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

OUT="$ROOT/build/dll2klib-e2e"
rm -rf "$OUT"
mkdir -p "$OUT/tools" "$OUT/klib" "$OUT/klib-second" "$OUT/bir" "$OUT/cir" "$OUT/il"
case "${OS:-}" in
	Windows_NT) KLIB_CP_SEP=';' ;;
	*) KLIB_CP_SEP=':' ;;
esac

need_kotc
need_fe_klib
build_tool bir2cir
build_tool ilemit
need_stdlib_ref
need_stdlib_rt
need_dotnet_reference_sets

dotnet build "$ROOT/toolchain/dll2klib/dll2klib.csproj" -c Release -o "$OUT/tools" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-e2e/reference/Probe.csproj" -c Release -v:q --nologo

PROBE_REF="$ROOT/tests/special/dll2klib-e2e/reference/obj/Release/net10.0/ref/Probe.dll"
PROBE_IMPL="$ROOT/tests/special/dll2klib-e2e/reference/bin/Release/net10.0/Probe.dll"
CONTRACTS_REF="$ROOT/tests/special/dll2klib-e2e/reference/obj/Release/net10.0/ref/Probe.Contracts.dll"
CONTRACTS_IMPL="$ROOT/tests/special/dll2klib-e2e/reference/bin/Release/net10.0/Probe.Contracts.dll"
PROBE_KLIB="$OUT/klib/Probe.klib"
CONTRACTS_KLIB="$OUT/klib/Probe.Contracts.klib"

# The two-path form is an internal worker protocol. Without the batch parent's complete resolved catalog it cannot
# identify external delegate or Kotlin companion TypeRefs and must fail rather than silently project their physical
# CLR carriers as ordinary nominal classes.
direct_out="$OUT/direct-Probe.klib"
if direct_error="$(dotnet "$OUT/tools/dll2klib.dll" "$PROBE_REF" "$direct_out" 2>&1)"; then
	die "standalone direct worker invocation unexpectedly succeeded without resolved reference catalogs"
fi
grep -q "direct worker mode requires the batch-provided resolved delegate, companion, and inner catalogs" <<<"$direct_error" \
	|| die "standalone direct worker rejection did not explain the required batch reference set"
[[ ! -e "$direct_out" ]] || die "rejected standalone direct worker invocation still wrote a KLIB"

# Both stdlib CLR twins carry a semantic library-kind marker. A human asking for a direct projection gets an
# actionable warning and no duplicate KLIB; the response-file/MSBuild reference-set path ignores the same inputs
# silently because the authoritative frontend stdlib KLIB is already on kotc's classpath.
for stdlib in "$STDLIB_REF_DLL" "$STDLIB_RT_DLL"; do
	stdlib_out="$OUT/$(basename "${stdlib%.dll}").klib"
	stdlib_warning="$(dotnet "$OUT/tools/dll2klib.dll" "$stdlib" "$stdlib_out" 2>&1)"
	grep -q "warning: ignored Kotlin standard library assembly" <<<"$stdlib_warning" \
		|| die "$(basename "$stdlib") lacks DotKt.LibraryKind=stdlib or direct dll2klib did not warn"
	[[ ! -e "$stdlib_out" ]] \
		|| die "direct dll2klib projected marked stdlib $(basename "$stdlib")"
done
printf '%s\n%s\n' "$STDLIB_REF_DLL" "$STDLIB_RT_DLL" > "$OUT/stdlib-references.rsp"
stdlib_batch_stderr="$OUT/stdlib-batch.err"
stdlib_batch="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/stdlib-klib" @"$OUT/stdlib-references.rsp" 2>"$stdlib_batch_stderr")"
[[ ! -s "$stdlib_batch_stderr" ]] \
	|| die "response-file dll2klib warned while silently ignoring marked stdlib inputs"
grep -q '0 KLIB(s) up to date' <<<"$stdlib_batch" \
	|| die "response-file dll2klib did not remove marked stdlib inputs from the projection set"
[[ -z "$(find "$OUT/stdlib-klib" -maxdepth 1 -name '*.klib' -print -quit)" ]] \
	|| die "response-file dll2klib projected a marked stdlib assembly"

printf '%s\n%s\n' "$PROBE_REF" "$CONTRACTS_REF" > "$OUT/references.rsp"
dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp"
dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib-second" --jobs 0 @"$OUT/references.rsp"
cmp -s "$PROBE_KLIB" "$OUT/klib-second/Probe.klib" \
	|| die "same Probe MVID did not produce a deterministic KLIB"
cmp -s "$CONTRACTS_KLIB" "$OUT/klib-second/Probe.Contracts.klib" \
	|| die "same contracts MVID did not produce a deterministic KLIB"
cache_hit="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp")"
grep -q '2 KLIB(s) up to date' <<<"$cache_hit" \
	|| die "unchanged reference set did not hit the per-assembly KLIB cache"
sleep 1
touch "$CONTRACTS_REF"
dependency_rebuild="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp")"
grep -q 'converting 2/2 reference(s)' <<<"$dependency_rebuild" \
	|| die "external delegate change did not invalidate the consuming Probe KLIB"
# Removing or adding an input can change the shared arity/delegate/companion catalog without changing any surviving DLL's
# timestamp. Every surviving KLIB must be regenerated so cached and newly projected declarations keep one naming
# universe.
printf '%s\n' "$PROBE_REF" > "$OUT/references.rsp"
catalog_remove="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp")"
grep -q 'converting 1/1 reference(s)' <<<"$catalog_remove" \
	|| die "reference-catalog removal did not invalidate the surviving Probe KLIB"
printf '%s\n%s\n' "$PROBE_REF" "$CONTRACTS_REF" > "$OUT/references.rsp"
catalog_restore="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp")"
grep -q 'converting 2/2 reference(s)' <<<"$catalog_restore" \
	|| die "reference-catalog restoration did not invalidate the complete KLIB set"
for entry in default/manifest default/linkdata/module default/linkdata/root_package/0_.knm default/linkdata/package_Probe/0_Probe.knm; do
	unzip -Z1 "$PROBE_KLIB" | grep -qx "$entry" || die "generated KLIB is missing $entry"
done

# The manifest uses an ordinary unique_name, while KlibMetadataProtoBuf.Header.module_name is a Kotlin Name and must
# therefore use the special `<...>` spelling. A plain header name happens to deserialize as protobuf but is rejected
# by standard loader paths that construct module data from it.
manifest_unique_name="$(unzip -p "$PROBE_KLIB" default/manifest | sed -n 's/^unique_name=//p')"
module_header_name="$(python3 - "$PROBE_KLIB" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as klib:
    data = klib.read("default/linkdata/module")
if not data or data[0] != 0x0A:  # field 1, wire type 2: module_name
    raise SystemExit("KLIB header does not begin with module_name")
offset = 1
size = shift = 0
while True:
    byte = data[offset]
    offset += 1
    size |= (byte & 0x7F) << shift
    if byte < 0x80:
        break
    shift += 7
print(data[offset:offset + size].decode("utf-8"))
PY
)"
[[ -n "$manifest_unique_name" && "$module_header_name" == "<$manifest_unique_name>" ]] \
	|| die "KLIB header module_name '$module_header_name' is not the special form of manifest unique_name '$manifest_unique_name'"

# The only classpath metadata for Probe.Widget is the packed KLIB.
"$KOTC" "$ROOT/tests/special/dll2klib-e2e/consumer.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" -d "$OUT/bir"

# CLR statics are direct KLIB declarations. A plain CLR owner must not acquire a companion classifier/value merely
# because it has static members; otherwise source can silently depend on projection scaffolding that does not exist in
# CLR metadata. Keep this as a negative frontend probe alongside the positive Widget.Global / Widget.Twice uses above.
no_companion_log="$OUT/no-synthetic-companion.log"
if "$KOTC" "$ROOT/tests/special/dll2klib-e2e/no-synthetic-companion.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" -d "$OUT/no-synthetic-companion-bir" \
	>"$no_companion_log" 2>&1; then
	die "plain CLR static owner unexpectedly exposed Widget.Companion"
fi
grep -q "unresolved reference.*Companion" "$no_companion_log" \
	|| die "Widget.Companion was rejected for an unexpected reason"

compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$PROBE_REF" "$CONTRACTS_REF")"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$compile_refs" "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" Consumer \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$PROBE_REF" "$CONTRACTS_REF")" \
	--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL")" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/cir/consumer.cir.json"
write_runtimeconfig "$OUT/il" Consumer
cp "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL" "$OUT/il/"

actual="$(dotnet "$OUT/il/Consumer.dll")"
[[ "$actual" == "132" ]] || die "generated program returned '$actual', expected '132'"
grep -q '"k": "clrInstance"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR instance member"
grep -q '"k": "clrStatic"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR static member"

info "PASS  CLR ref.dll -> standard KLIB (types, nested types, members incl. inherited instance/static properties, generics, NRT, local/cross-assembly delegates, indexers, events, extensions, operators, byref) -> kotc -> bir2cir -> ilemit -> run (132)"
