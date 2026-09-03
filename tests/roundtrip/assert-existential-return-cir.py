#!/usr/bin/env python3
"""Assert exact referenced existential result projection, including star-dependent nested carriers."""

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
if casts[0].get("type") != semantic:
    raise SystemExit(f"referenced result is not projected to the consumer's semantic type: {casts[0]!r}")

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
nested_locals = [
    node
    for node in body
    if isinstance(node, dict)
    and node.get("k") == "var"
    and node.get("type") == nested_carrier
    and isinstance(node.get("init"), dict)
    and node["init"].get("k") == "cast"
    and node["init"].get("type") == nested_carrier
    and node["init"].get("e", {}).get("ownerType", {}).get("name")
    == "starprojection.ReferencedStarNestedCopy$star"
]
if len(nested_locals) != 1:
    raise SystemExit(
        f"referenced nested local must retain the physical carrier: {nested_locals!r}"
    )

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

value_getters = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "starprojection.ReferencedStarNested$star"
    and node.get("ret", {}).get("name") == "System.Object"
]
if len(value_getters) != 2:
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

mixed_calls = {
    node.get("method"): node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "starprojection.MixedBox$star"
    and node.get("method") in {"$star$capturedNested$2", "$star$exactNested$3"}
}
if set(mixed_calls) != {"$star$capturedNested$2", "$star$exactNested$3"}:
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

exact_nested = {
    "t": "fqn",
    "name": "starprojection.ReferencedStarNested`1",
    "args": [{"t": "fqn", "name": "System.String"}],
}
expected_mixed_locals = {
    "$star$capturedNested$2": nested_carrier,
    "$star$exactNested$3": exact_nested,
}
for method_name, projected_type in expected_mixed_locals.items():
    mixed_call = mixed_calls[method_name]
    matching_locals = [
        node
        for node in body
        if isinstance(node, dict)
        and node.get("k") == "var"
        and node.get("type") == projected_type
        and isinstance(node.get("init"), dict)
        and node["init"].get("k") == "cast"
        and node["init"].get("type") == projected_type
        and node["init"].get("e") is mixed_call
    ]
    if len(matching_locals) != 1:
        raise SystemExit(
            f"{method_name} must project to {projected_type!r}: {matching_locals!r}"
        )

print("referenced existential results preserve exact and star-dependent physical projections")
