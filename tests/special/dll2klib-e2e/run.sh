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
dotnet build "$ROOT/tests/roundtrip/metadata-inspector/CompanionMetadataInspector.csproj" \
	-c Release -o "$OUT/tools/metadata-inspector" -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-e2e/reference/Probe.csproj" -c Release -v:q --nologo
dotnet build "$ROOT/tests/special/dll2klib-e2e/transitive-reference/TransitiveReferenceGenerator.csproj" \
	-c Release -o "$OUT/tools/transitive-reference" -v:q --nologo

PROBE_REF="$ROOT/tests/special/dll2klib-e2e/reference/obj/Release/net10.0/ref/Probe.dll"
PROBE_IMPL="$ROOT/tests/special/dll2klib-e2e/reference/bin/Release/net10.0/Probe.dll"
CONTRACTS_REF="$ROOT/tests/special/dll2klib-e2e/reference/obj/Release/net10.0/ref/Probe.Contracts.dll"
CONTRACTS_IMPL="$ROOT/tests/special/dll2klib-e2e/reference/bin/Release/net10.0/Probe.Contracts.dll"
PROBE_KLIB="$OUT/klib/Probe.klib"
CONTRACTS_KLIB="$OUT/klib/Probe.Contracts.klib"
TRANSITIVE_REF="$OUT/TransitiveSlotProbe.dll"

dotnet "$OUT/tools/transitive-reference/TransitiveReferenceGenerator.dll" "$TRANSITIVE_REF"
printf '%s\n' "$TRANSITIVE_REF" > "$OUT/transitive-references.rsp"
dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/transitive-klib" --jobs 0 @"$OUT/transitive-references.rsp"
"$KOTC" "$ROOT/tests/special/dll2klib-e2e/transitive-interface-consumer.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$OUT/transitive-klib/TransitiveSlotProbe.klib" \
	-d "$OUT/transitive-bir"

# The two-path form is an internal worker protocol. Without the batch parent's complete resolved catalog it cannot
# identify external delegate or Kotlin companion TypeRefs and must fail rather than silently project their physical
# CLR carriers as ordinary nominal classes.
direct_out="$OUT/direct-Probe.klib"
if direct_error="$(dotnet "$OUT/tools/dll2klib.dll" "$PROBE_REF" "$direct_out" 2>&1)"; then
	die "standalone direct worker invocation unexpectedly succeeded without resolved reference catalogs"
fi
grep -q "direct worker mode requires the batch-provided resolved delegate, companion, inner, and public-type catalogs" <<<"$direct_error" \
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
if catalog_remove="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp" 2>&1)"; then
	die "dll2klib accepted Probe without its referenced Probe.Contracts assembly"
fi
grep -q "public-type catalog cannot resolve 'Probe.Contracts.IExternalDefaultSlot'" <<<"$catalog_remove" \
	|| die "incomplete reference-catalog rejection did not identify the unresolved public supertype"
printf '%s\n%s\n' "$PROBE_REF" "$CONTRACTS_REF" > "$OUT/references.rsp"
catalog_restore="$(dotnet "$OUT/tools/dll2klib.dll" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp")"
grep -q '2 KLIB(s) up to date' <<<"$catalog_restore" \
	|| die "rejected incomplete reference catalog corrupted the complete KLIB cache"
for entry in default/manifest default/linkdata/module default/linkdata/root_package/0_.knm default/linkdata/package_Probe/0_Probe.knm; do
	unzip -Z1 "$PROBE_KLIB" | grep -qx "$entry" || die "generated KLIB is missing $entry"
done
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-csharp-extension-shape "$PROBE_KLIB" Probe Probe.WidgetExtensions Bump
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-csharp-extension-shape "$PROBE_KLIB" "" GlobalWidgetExtensions GlobalBump
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.VisibilityProbe \
	Probe.Contracts.IVisibleGeneric,Probe.IVisibleControl
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.DefaultCarrier1 Probe.IPublicDefaultSlot
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.DefaultCarrier2 Probe.IPublicDefaultSlot
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.ConstructedDefaultCarrier Probe.IPublicDefaultSlot
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.GenericDefaultCarrier Probe.IPublicGenericDefaultSlot
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.ExternalDefaultCarrier Probe.Contracts.IExternalDefaultSlot
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-function-nullability "$PROBE_KLIB" Probe.NullabilityDefaultCarrier Normalize true true
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-properties "$PROBE_KLIB" Probe.DefaultEventCarrier Changed
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-functions "$PROBE_KLIB" Probe.DefaultEventCarrier ""
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-properties "$PROBE_KLIB" Probe.ExplicitEventCarrier Changed
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-properties "$PROBE_KLIB" Probe.ExternalExplicitEventCarrier Changed
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-properties "$PROBE_KLIB" Probe.PublicAndExplicitEventCarrier Changed,Changed
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-function-nullability "$PROBE_KLIB" Probe.ExplicitShapeCarrier Normalize true true
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-properties "$PROBE_KLIB" Probe.ExplicitShapeCarrier Text
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-functions "$PROBE_KLIB" Probe.ExplicitShapeCarrier Normalize,get
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-functions "$PROBE_KLIB" Probe.StaticAndExplicitMethodCarrier Read,Read
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-functions "$PROBE_KLIB" Probe.DefaultIndexerCarrier get,get
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-functions "$PROBE_KLIB" Probe.ExplicitIndexerCarrier get,get
dotnet "$OUT/tools/metadata-inspector/CompanionMetadataInspector.dll" \
	--klib-class-supertypes "$PROBE_KLIB" Probe.ProtectedInterfaceOwner.Impl Probe.ProtectedInterfaceOwner.IState

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

# KLIB upper bounds own Kotlin-nominal rows. CLR class/struct/new() flags and the implicit ValueType/Enum rows have
# no faithful Kotlin nominal encoding, so kotc accepts these source shapes and bir2cir must reject the invalid physical
# TypeSpecs against the authoritative reference metadata instead of leaving a TypeLoadException for runtime.
expect_constraint_failure() {
	local name="$1" owner="$2" requirement="$3"
	local bir="$OUT/constraint-$name-bir" cir="$OUT/constraint-$name-cir" log="$OUT/constraint-$name.log"
	mkdir -p "$bir" "$cir"
	"$KOTC" "$ROOT/tests/special/dll2klib-e2e/invalid-$name-constraint.kt" \
		-no-stdlib \
		-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" -d "$bir"
	if dotnet "$BIR2CIR_DLL" "$cir" --compile-refs "$compile_refs" "$bir"/*.bir.json >"$log" 2>&1; then
		die "invalid $name generic constraint unexpectedly reached CIR"
	fi
	grep -q "CLR generic constraint violation" "$log" \
		|| die "invalid $name generic constraint lacked the physical-constraint diagnostic"
	grep -q "$owner" "$log" \
		|| die "invalid $name generic constraint diagnostic did not name $owner"
	grep -q "$requirement" "$log" \
		|| die "invalid $name generic constraint diagnostic did not state $requirement"
}

compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$PROBE_REF" "$CONTRACTS_REF")"
expect_constraint_failure class ReferenceConstraintBox "reference type"
expect_constraint_failure struct StructConstraintBox "value type"
expect_constraint_failure enum EnumConstraintBox "enum type"
expect_constraint_failure new FreshConstraintBox "parameterless constructor"
expect_constraint_failure new-alias FreshConstraintBox "parameterless constructor"
expect_constraint_failure nested-class ReferenceConstraintBox "reference type"
expect_constraint_failure member-class MemberConstraintApi.Reference "reference type"
expect_constraint_failure member-struct MemberConstraintApi.Struct "value type"
expect_constraint_failure member-enum MemberConstraintApi.Enum "enum type"
expect_constraint_failure member-new MemberConstraintApi.Fresh "parameterless constructor"
expect_constraint_failure member-open-class MemberConstraintApi.Reference "reference type"
expect_constraint_failure member-extension-struct WidgetExtensions.ConstrainedValue "value type"
expect_constraint_failure member-unmanaged MemberConstraintApi.Unmanaged "value type"
expect_constraint_failure member-static-delegate MemberConstraintApi.Struct "value type"
expect_constraint_failure member-bound-delegate MemberConstraintHost.Struct "value type"
expect_constraint_failure member-inherited-class IMemberConstraintSlot.Reference "reference type"
expect_constraint_failure member-constrained-class IMemberConstraintSlot.Reference "reference type"

# The mixed struct + nominal row proves the common projection policy removes only the physical ValueType root.
# The ordinary interface bound must remain visible to kotc and reject Int before bir2cir is involved.
nominal_log="$OUT/constraint-member-nominal.log"
if "$KOTC" "$ROOT/tests/special/dll2klib-e2e/invalid-member-nominal-constraint.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" \
	-d "$OUT/constraint-member-nominal-bir" >"$nominal_log" 2>&1; then
	die "dll2klib dropped the nominal IConstraintMarker method bound"
fi
grep -q "IConstraintMarker" "$nominal_log" \
	|| die "nominal method-constraint rejection did not name IConstraintMarker"

# A CLR explicit implementation satisfies an interface slot but is not an ordinary class API. Method/property/
# indexer/event collisions therefore remain reachable only through the exact interface, while a derived Kotlin class
# may re-list one interface and supply a new implementation without changing the mappings it merely inherits.
explicit_direct_log="$OUT/explicit-slot-direct.log"
if "$KOTC" "$ROOT/tests/special/dll2klib-e2e/explicit-slot-direct-call.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" \
	-d "$OUT/explicit-slot-direct-bir" >"$explicit_direct_log" 2>&1; then
	die "explicit interface slots unexpectedly became ordinary class APIs"
fi
for member in Pick Number Updated; do
	grep -q "unresolved reference.*$member" "$explicit_direct_log" \
		|| die "explicit $member class API was rejected for an unexpected reason"
done
grep -q "explicit-slot-direct-call.kt:12:27: error: unresolved reference.*receiver type mismatch" \
	"$explicit_direct_log" \
	|| die "explicit indexer class API was rejected for an unexpected reason"

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

reabstract_log="$OUT/reabstract-interface.log"
if "$KOTC" "$ROOT/tests/special/dll2klib-e2e/reabstract-interface-consumer.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" \
	-d "$OUT/reabstract-interface-bir" >"$reabstract_log" 2>&1; then
	die "reabstracted interface slots unexpectedly appeared concrete"
fi
for member in MissingReabstractMethod MissingReabstractProperty MissingReabstractEvent; do
	grep -q "class '$member'.*does not implement abstract member" "$reabstract_log" \
		|| die "$member was rejected for an unexpected reason"
done

mkdir -p "$OUT/default-bir" "$OUT/default-cir" "$OUT/default-il" \
	"$OUT/explicit-slot-bir" "$OUT/explicit-slot-cir" "$OUT/explicit-slot-il"
"$KOTC" "$ROOT/tests/special/dll2klib-e2e/default-interface-consumer.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" -d "$OUT/default-bir"
dotnet "$BIR2CIR_DLL" "$OUT/default-cir" --compile-refs "$compile_refs" \
	"$OUT/default-bir/default-interface-consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/default-il" DefaultInterfaceConsumer \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$PROBE_REF" "$CONTRACTS_REF")" \
	--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL")" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/default-cir/default-interface-consumer.cir.json"
write_runtimeconfig "$OUT/default-il" DefaultInterfaceConsumer
cp "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL" "$OUT/default-il/"
default_actual="$(dotnet "$OUT/default-il/DefaultInterfaceConsumer.dll")"
[[ "$default_actual" == "236" ]] \
	|| die "hidden/default/reabstracted interface program returned '$default_actual', expected '236'"
"$KOTC" "$ROOT/tests/special/dll2klib-e2e/explicit-slot-probe.kt" \
	-no-stdlib \
	-classpath "$FE_KLIB$KLIB_CP_SEP$PROBE_KLIB$KLIB_CP_SEP$CONTRACTS_KLIB" -d "$OUT/explicit-slot-bir"
dotnet "$BIR2CIR_DLL" "$OUT/explicit-slot-cir" --compile-refs "$compile_refs" \
	"$OUT/explicit-slot-bir/explicit-slot-probe.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/explicit-slot-il" ExplicitSlotProbe \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$PROBE_REF" "$CONTRACTS_REF")" \
	--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL")" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/explicit-slot-cir/explicit-slot-probe.cir.json"
write_runtimeconfig "$OUT/explicit-slot-il" ExplicitSlotProbe
cp "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL" "$OUT/explicit-slot-il/"
explicit_slot_actual="$(dotnet "$OUT/explicit-slot-il/ExplicitSlotProbe.dll")"
[[ "$explicit_slot_actual" == "463" ]] \
	|| die "explicit interface slot program returned '$explicit_slot_actual', expected '463'"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/explicit-slot-il/ExplicitSlotProbe.dll"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$compile_refs" "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" Consumer \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$PROBE_REF" "$CONTRACTS_REF")" \
	--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL")" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/cir/consumer.cir.json"
write_runtimeconfig "$OUT/il" Consumer
cp "$STDLIB_RT_DLL" "$PROBE_IMPL" "$CONTRACTS_IMPL" "$OUT/il/"

actual="$(dotnet "$OUT/il/Consumer.dll")"
[[ "$actual" == "508" ]] || die "generated program returned '$actual', expected '508'"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/il/Consumer.dll"
grep -q '"k": "clrInstance"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR instance member"
grep -q '"k": "clrStatic"' "$OUT/cir/consumer.cir.json" \
	|| die "bir2cir did not bind the KLIB declaration to a CLR static member"
if grep -q '"_resolvedMethodTypeParams"' "$OUT/cir/consumer.cir.json"; then
	die "bir2cir leaked its resolved-method constraint carrier into CIR"
fi

info "PASS  CLR ref.dll -> standard KLIB (types, nested types, members incl. inherited instance/static properties, generic constraints, public-only interface supertypes, generics, NRT, local/cross-assembly delegates, indexers, events, extensions, operators, byref) -> kotc -> bir2cir -> ilemit -> run (508)"
