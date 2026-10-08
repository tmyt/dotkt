#!/usr/bin/env python3
"""Assert exact referenced existential result projection, including star-dependent nested carriers."""

import base64
import hashlib
import json
import sys


def objects(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from objects(value)
    elif isinstance(node, list):
        for value in node:
            yield from objects(value)


def value_slot(slot):
    # Signature modifiers select declarations; the stack value is the underlying
    # slot. Do not remove modifiers nested inside a constructed type argument.
    while slot.get("t") == "mod":
        slot = slot["of"]
    return slot


if len(sys.argv) != 2:
    raise SystemExit("usage: assert-existential-return-cir.py <CrossModuleMetadataTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

if any(node.get("_existentialResultProjection") is not None for node in objects(root)):
    raise SystemExit("CIR: unconsumed existential-result projection fact")

methods = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "fuseReferencedExistential"
]
if len(methods) != 1:
    raise SystemExit(f"found {len(methods)} fuseReferencedExistential methods, expected 1")

calls = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name")
    == "starprojection.ReferencedExistentialFusibleFlow$star"
]
if len(calls) != 1:
    raise SystemExit(f"found {len(calls)} referenced existential fuse calls, expected 1: {calls!r}")

call = calls[0]
physical = {"t": "fqn", "name": "starprojection.ReferencedExistentialFlow$star"}
if call.get("method") != "$star$fuse$0" or call.get("ret") != physical:
    raise SystemExit(f"referenced call does not state the physical slot result: {call!r}")
member_ref = call.get("memberRef", {})
if (
    call.get("virtual") is not True
    or member_ref.get("declaringType") != call.get("ownerType")
    or member_ref.get("returnType") != physical
):
    raise SystemExit(f"referenced call/memberRef disagree with the physical slot: {call!r}")

casts = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "cast" and node.get("e") is call
]
if len(casts) != 1:
    raise SystemExit(f"found {len(casts)} semantic projections around referenced fuse, expected 1")

semantic = {
    "t": "fqn",
    "name": "starprojection.ReferencedExistentialFlow`1",
    "args": [{"t": "tv", "scope": "method", "i": 0}],
}
def assert_source_result(method):
    if method.get("ret") != physical or len(method.get("typeParams", [])) != 1:
        raise SystemExit(f"referenced result lost its carrier or method frame: {method!r}")
    for attribute_name in ("KotlinTypeAttribute", "KotlinNullableGenericAttribute"):
        carriers = [attribute for attribute in method.get("retAttrs", [])
                    if attribute.get("attr", {}).get("name")
                    == f"DotKt.Runtime.CompilerServices.{attribute_name}"]
        if len(carriers) != 1:
            raise SystemExit(f"referenced result must retain one {attribute_name}: {method!r}")
        arguments = carriers[0].get("args", [])
        if (len(arguments) != 2 or arguments[0].get("value") != "bir-json/1"
                or json.loads(base64.b64decode(arguments[1]["bytes"], validate=True)) != semantic):
            raise SystemExit(f"referenced result lost its source Flow<T> method frame: {method!r}")


# Value slots and casts use the physical carrier. The original Kotlin application
# belongs to declaration metadata, not to a second reified CLR result type.
assert_source_result(methods[0])
if casts[0].get("type") != physical:
    raise SystemExit(f"referenced result cast does not use its physical carrier: {casts[0]!r}")

referenced_exact = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "exactReferencedExistentialUpcast"
]
if len(referenced_exact) != 1:
    raise SystemExit(
        f"found {len(referenced_exact)} exactReferencedExistentialUpcast methods, expected 1"
    )
exact_casts = [
    node
    for node in objects(referenced_exact[0].get("body", []))
    if node.get("k") == "cast" and node.get("type") == physical
]
assert_source_result(referenced_exact[0])
if len(exact_casts) != 1:
    raise SystemExit(
        f"referenced generic upcast did not retain its physical carrier: {exact_casts!r}"
    )

referenced_composed = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "composedReferencedExistentialUpcast"
]
if len(referenced_composed) != 1:
    raise SystemExit(
        f"found {len(referenced_composed)} composedReferencedExistentialUpcast methods, expected 1"
    )
composed_casts = [
    node.get("type", {}).get("name")
    for node in objects(referenced_composed[0].get("body", []))
    if node.get("k") == "cast"
]
if sorted(composed_casts) != sorted([
    "System.Object",
    "starprojection.ReferencedExistentialFusibleFlow$star",
    "starprojection.ReferencedExistentialFlow$star",
]):
    raise SystemExit(
        "referenced composed unchecked cast reclassified its erased operand as exact: "
        f"{composed_casts!r}"
    )

roundtrip_tests = [
    method
    for method in objects(root)
    if method.get("name") == "boundedStarProjectionRoundTrips"
    and isinstance(method.get("body"), list)
]
if len(roundtrip_tests) != 1:
    raise SystemExit(
        f"found {len(roundtrip_tests)} boundedStarProjectionRoundTrips methods, expected 1"
    )

body = roundtrip_tests[0]["body"]
forbidden_object_casts = [
    node
    for node in objects(body)
    if node.get("k") == "cast"
    and isinstance(node.get("type"), dict)
    and node["type"].get("name") == "starprojection.ReferencedStarNested`1"
    and node["type"].get("args") == [{"t": "fqn", "name": "System.Object"}]
]
if forbidden_object_casts:
    raise SystemExit(
        "referenced star-dependent results must not cast to Nested<object>: "
        f"{forbidden_object_casts!r}"
    )

nested_carrier = {"t": "fqn", "name": "starprojection.ReferencedStarNested$star"}
nested_getters = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name")
    == "starprojection.ReferencedStarNestedCopy$star"
    and node.get("ret") == nested_carrier
]
if len(nested_getters) != 2:
    raise SystemExit(
        "referenced copy default and ordinary nested getter must return the exact carrier: "
        f"{nested_getters!r}"
    )
for nested_getter in nested_getters:
    member_ref = nested_getter.get("memberRef", {})
    if (
        nested_getter.get("virtual") is not True
        or member_ref.get("declaringType") != nested_getter.get("ownerType")
        or member_ref.get("returnType") != nested_carrier
    ):
        raise SystemExit(
            f"referenced nested getter/memberRef disagree with the carrier slot: {nested_getter!r}"
        )

nested_chained_locals = [
    node
    for node in body
    if isinstance(node, dict)
    and node.get("k") == "var"
    and node.get("type") == nested_carrier
    and isinstance(node.get("init"), dict)
    and node["init"].get("k") == "cast"
    and node["init"].get("type") == nested_carrier
    and any(candidate is nested_getter for candidate in objects(node["init"])
            for nested_getter in nested_getters)
]
if len(nested_chained_locals) != 1:
    raise SystemExit(
        "referenced nested getter followed by another owner-dependent result must retain the carrier: "
        f"{nested_chained_locals!r}"
    )

again_calls = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "starprojection.ReferencedStarNested$star"
    and str(node.get("method", "")).startswith("$star$again$")
    and node.get("ret") == nested_carrier
]
if len(again_calls) != 3:
    raise SystemExit(f"copy and both mixed chained results must use the carrier: {again_calls!r}")
for again_call in again_calls:
    member_ref = again_call.get("memberRef", {})
    if (
        again_call.get("virtual") is not True
        or member_ref.get("declaringType") != again_call.get("ownerType")
        or member_ref.get("returnType") != nested_carrier
    ):
        raise SystemExit(f"chained result/memberRef disagree with the carrier slot: {again_call!r}")

value_getters = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "starprojection.ReferencedStarNested$star"
    and node.get("ret", {}).get("name") == "System.Object"
]
if len(value_getters) != 4:
    raise SystemExit(
        f"referenced nested value use must bind through the existential carrier: {value_getters!r}"
    )
for value_getter in value_getters:
    value_ref = value_getter.get("memberRef", {})
    if (
        value_getter.get("virtual") is not True
        or value_ref.get("declaringType") != value_getter.get("ownerType")
        or value_ref.get("returnType") != value_getter.get("ret")
    ):
        raise SystemExit(
            f"referenced nested value/memberRef disagree with the physical slot: {value_getter!r}"
        )

string_type = {"t": "fqn", "name": "System.String"}
length_uses = [node for node in objects(body)
               if node.get("k") == "clrPropGet" and node.get("type") == string_type
               and node.get("name") == "Length"
               and any(child in value_getters for child in objects(node.get("recv")))]
if len(length_uses) != 1:
    raise SystemExit("concrete nested String must consume its carrier result through Length")
string_receiver = length_uses[0].get("recv", {})
temps = [node for node in string_receiver.get("stmts", [])
         if node.get("k") == "var"
         and any(child in value_getters for child in objects(node.get("init")))]
if len(temps) != 1 or string_receiver.get("result", {}).get("type") != string_type:
    raise SystemExit("nested String use must evaluate the carrier getter once and project its result")
string_projections = [node for node in objects(string_receiver.get("result"))
                      if node.get("k") == "cast" and node.get("type") == string_type
                      and node.get("e") == {"k": "local", "name": temps[0]["name"]}]
if len(string_projections) != 1:
    raise SystemExit("concrete nested String use lost its checked projection from the captured carrier result")
length_member = length_uses[0].get("memberRef", {})
if (length_member.get("declaringType") != string_type
        or length_member.get("name") != "get_Length"
        or length_member.get("returnType") != {"t": "fqn", "name": "System.Int32"}
        or length_member.get("parameterTypes") != []):
    raise SystemExit("concrete nested String use must retain its exact native Length accessor")

mixed_calls = {}
for node in objects(body):
    if (
        node.get("k") != "callInstance"
        or node.get("ownerType", {}).get("name") != "starprojection.MixedBox$star"
    ):
        continue
    for source_name in ("capturedNested", "exactNested"):
        if str(node.get("method", "")).startswith(f"$star${source_name}$"):
            if source_name in mixed_calls:
                raise SystemExit(f"duplicate mixed {source_name} call: {node!r}")
            mixed_calls[source_name] = node
if set(mixed_calls) != {"capturedNested", "exactNested"}:
    raise SystemExit(f"mixed star/exact nested calls are incomplete: {mixed_calls!r}")
for method_name, mixed_call in mixed_calls.items():
    member_ref = mixed_call.get("memberRef", {})
    if (
        mixed_call.get("ret") != nested_carrier
        or mixed_call.get("virtual") is not True
        or member_ref.get("declaringType") != mixed_call.get("ownerType")
        or member_ref.get("returnType") != nested_carrier
    ):
        raise SystemExit(
            f"mixed nested call/memberRef must state the physical carrier slot: {mixed_call!r}"
        )

for source_name in ("capturedNested", "exactNested"):
    projected_type = nested_carrier
    mixed_call = mixed_calls[source_name]
    matching_locals = [
        node
        for node in body
        if isinstance(node, dict)
        and node.get("k") == "var"
        and node.get("type") == projected_type
        and isinstance(node.get("init"), dict)
        and any(candidate is mixed_call for candidate in objects(node["init"]))
    ]
    if len(matching_locals) != 1:
        raise SystemExit(
            f"{source_name} must remain {projected_type!r} through the chained call: {matching_locals!r}"
        )

def assert_referenced_variant_array(stem, carrier_name, minimum_ops):
    carrier = {"t": "array", "elem": {"t": "fqn", "name": carrier_name}}
    new_name = f"new{stem}"
    write_name = f"write{stem}"
    calls = {
        node.get("method"): node
        for node in objects(body)
        if node.get("k") == "callStatic" and node.get("method") in (new_name, write_name)
    }
    if set(calls) != {new_name, write_name}:
        raise SystemExit(f"referenced variant-array calls are incomplete: {calls!r}")
    if (
        calls[new_name].get("ret") != carrier
        or calls[new_name].get("memberRef", {}).get("returnType") != carrier
        or calls[write_name].get("sig") != [carrier]
        or calls[write_name].get("memberRef", {}).get("parameterTypes") != [carrier]
    ):
        raise SystemExit(
            f"referenced {stem} signatures did not use one physical carrier: {calls!r}"
        )
    array_ops = [
        node
        for node in objects(body)
        if node.get("k") in ("arrayGet", "arraySet")
        and node.get("elem", {}).get("name") == carrier_name
    ]
    if len(array_ops) < minimum_ops:
        raise SystemExit(
            f"referenced {stem} reads/writes did not retain the carrier: {array_ops!r}"
        )


assert_referenced_variant_array(
    "ReferencedCovariantClassArray",
    "starprojection.ReferencedCovariantArrayClass$star",
    6,
)
assert_referenced_variant_array(
    "ReferencedUnsafeArray",
    "starprojection.ReferencedUnsafeArrayValue$star",
    6,
)

# A referenced Kotlin class's declaration-site variance remains Kotlin metadata. The consuming module must use
# the producer's non-generic existential carrier at widened value boundaries without weakening exact constructor
# heads or trying to model the source variance as CLR class variance.
referenced_covariant_carrier = {
    "t": "fqn",
    "name": "starprojection.ReferencedCovariantArrayClass$star",
}
referenced_contravariant_carrier = {
    "t": "fqn",
    "name": "starprojection.ReferencedContravariantClass$star",
}

variant_constructions = [
    node
    for node in objects(body)
    if node.get("k") == "new"
    and node.get("type", {}).get("name") in {
        "starprojection.ReferencedCovariantArrayClass`1",
        "starprojection.ReferencedContravariantClass`1",
    }
]
expected_constructions = {
    ("starprojection.ReferencedCovariantArrayClass`1", "System.Int32"),
    ("starprojection.ReferencedCovariantArrayClass`1", "System.String"),
    ("starprojection.ReferencedContravariantClass`1", "System.Object"),
}
observed_constructions = {
    (node["type"]["name"], node["type"].get("args", [{}])[0].get("name"))
    for node in variant_constructions
}
if observed_constructions != expected_constructions:
    raise SystemExit(
        f"referenced variant class constructors lost their exact constructed heads: {observed_constructions!r}"
    )

variant_member_calls = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType") in (referenced_covariant_carrier, referenced_contravariant_carrier)
]
if not variant_member_calls:
    raise SystemExit("referenced variant class members did not bind through existential carriers")
for variant_call in variant_member_calls:
    member_ref = variant_call.get("memberRef", {})
    if variant_call.get("ownerType") == referenced_covariant_carrier:
        expected_name = "$star$prop_get<value>$0"
        expected_signature = []
    else:
        expected_name = "$star$render$0"
        source_parameter = {"t": "tv", "scope": "type", "i": 0}
        source_owner = "starprojection.ReferencedContravariantClass"
        marker = "dotkt$ParameterSignature$" + hashlib.sha256(
            (source_owner + "|" + json.dumps(source_parameter, separators=(",", ":"))).encode("utf-8")
        ).hexdigest().upper()
        expected_signature = [{"t": "mod", "req": False,
                               "m": {"t": "fqn", "name": marker},
                               "of": {"t": "fqn", "name": "System.Object"}}]
    if (
        variant_call.get("virtual") is not True
        or variant_call.get("method") != expected_name
        or member_ref.get("kind") != "method"
        or member_ref.get("assembly") != "RoundtripProducer"
        or member_ref.get("name") != variant_call.get("method")
        or member_ref.get("declaringType") != variant_call.get("ownerType")
        or member_ref.get("returnType") != variant_call.get("ret")
        or member_ref.get("parameterTypes") != expected_signature
        or [value_slot(slot) for slot in member_ref.get("parameterTypes", [])]
        != variant_call.get("sig", [])
    ):
        raise SystemExit(f"referenced variant carrier call/memberRef disagree: {variant_call!r}")

variant_static_calls = {
    node.get("method"): node
    for node in objects(body)
    if node.get("k") == "callStatic"
    and node.get("method") in {
        "readReferencedCovariantClass",
        "newReferencedCovariantClassAsAny",
    }
}
if set(variant_static_calls) != {
    "readReferencedCovariantClass",
    "newReferencedCovariantClassAsAny",
}:
    raise SystemExit(f"referenced variant boundary calls are incomplete: {variant_static_calls!r}")
read_call = variant_static_calls["readReferencedCovariantClass"]
new_call = variant_static_calls["newReferencedCovariantClassAsAny"]
if (
    read_call.get("sig") != [referenced_covariant_carrier]
    or read_call.get("memberRef", {}).get("parameterTypes") != [referenced_covariant_carrier]
    or new_call.get("ret") != referenced_covariant_carrier
    or new_call.get("memberRef", {}).get("returnType") != referenced_covariant_carrier
):
    raise SystemExit(f"referenced variant boundaries did not use one physical carrier: {variant_static_calls!r}")

print("referenced existential results preserve exact and star-dependent physical projections")
