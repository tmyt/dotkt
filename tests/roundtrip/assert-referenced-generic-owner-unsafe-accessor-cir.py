#!/usr/bin/env python3
import json
import base64
import sys
from collections import Counter


def objects(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from objects(value)
    elif isinstance(node, list):
        for value in node:
            yield from objects(value)


if len(sys.argv) != 3:
    raise SystemExit(
        "usage: assert-referenced-generic-owner-unsafe-accessor-cir.py "
        "<ReferencedProtectedGenericOwner.cir.json> <ReferencedProtectedGenericOwnerTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    producer = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    root = json.load(stream)

holders = [
    item
    for item in root.get("types", [])
    if item.get("generated") and item.get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(holders) != 3:
    raise SystemExit(f"found {len(holders)} generic UnsafeAccessor holders, expected 3")

owner_tv = {"t": "tv", "scope": "type", "i": 0}
nullable_tv = {"t": "tv", "scope": "type", "i": 1}
base_open = {
    "t": "fqn",
    "name": "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase`2",
    "args": [owner_tv, nullable_tv],
}
physical_array = {"t": "fqn", "name": "System.Array"}
expected_params = [base_open, physical_array]

accessor_targets = []
for holder in holders:
    if holder.get("typeParams") != [
        {"name": "__owner0", "constraints": [nullable_tv]}, {"name": "__owner1"}
    ]:
        raise SystemExit(f"UnsafeAccessor holder lost the referenced owner's generic frame: {holder!r}")
    accessors = [
        method
        for method in holder.get("methods", [])
        if method.get("generated") and method.get("static") and method.get("extern")
    ]
    if len(accessors) != 1:
        raise SystemExit(f"found {len(accessors)} holder UnsafeAccessor methods, expected 1")
    accessor = accessors[0]
    if [parameter.get("type") for parameter in accessor.get("params", [])] != expected_params:
        raise SystemExit(
            f"UnsafeAccessor does not state the referenced MethodDef's physical parameters: {accessor!r}"
        )
    if accessor.get("ret") != physical_array:
        raise SystemExit(f"UnsafeAccessor does not state the referenced MethodDef's physical return: {accessor!r}")
    wrappers = [method for method in holder.get("methods", []) if method.get("name", "").endswith("$invoke")]
    if (len(wrappers) != 1 or wrappers[0].get("ret") != physical_array
            or [param.get("type") for param in wrappers[0].get("params", [])] != expected_params):
        raise SystemExit(f"UnsafeAccessor wrapper does not preserve the complete owner frame: {wrappers!r}")
    names = [
        argument.get("value", {}).get("value")
        for attribute in accessor.get("attrs", [])
        for argument in attribute.get("namedArgs", [])
        if argument.get("name") == "Name"
    ]
    if len(names) != 1:
        raise SystemExit(f"UnsafeAccessor has no exact target name: {accessor!r}")
    accessor_targets.append(names[0])

if Counter(accessor_targets) != Counter({"snapshot": 2, "openSnapshot": 1}):
    raise SystemExit(f"final/open protected target set is incomplete: {accessor_targets!r}")

holder_names = {holder.get("name") for holder in holders}
wrapper_calls = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name") in holder_names
    and node.get("method", "").endswith("$invoke")
]
if len(wrapper_calls) != 3:
    raise SystemExit(f"found {len(wrapper_calls)} UnsafeAccessor wrapper calls, expected 3")

constructed_frames = Counter(
    tuple(argument.get("name") for argument in call.get("owner", {}).get("args", []))
    for call in wrapper_calls
)
if constructed_frames != Counter({("System.String", "System.String"): 2, ("System.Int32", "object"): 1}):
    raise SystemExit(f"holder calls use the wrong referenced owner frames: {constructed_frames!r}")

for call in wrapper_calls:
    if call.get("sig") != expected_params or call.get("ret") != physical_array:
        raise SystemExit(f"holder call and physical accessor declaration disagree: {call!r}")
    concrete_array = {"t": "array", "elem": call["owner"]["args"][1]}
    if not any(node.get("k") == "cast" and node.get("type") == concrete_array
               and node.get("e") is call for node in objects(root.get("types", []))):
        raise SystemExit(f"concrete owner result has no explicit array conversion: {call!r}")
def facts(declaration, name):
    return [json.loads(base64.b64decode(attr["args"][1]["bytes"])) for attr in declaration.get("attrs", [])
            if attr.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


source_owner = "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase"
source = next(item for item in producer["types"] if item["name"] == source_owner)
original = next(method for method in source["methods"] if method["name"] == "snapshot")
assert original["vis"] == "protected" and facts(original, "KotlinCallableVisibility") == [{"visibility": "protected"}]
identity = facts(original, "KotlinDeclarationIdentity")[0]["id"]
carrier = next(item for item in producer["types"]
               if any(fact.get("name") == source_owner for fact in facts(item, "KotlinType")))
slot = next(method for method in carrier["methods"]
            if {"name": "snapshot", "sourceDeclaration": identity} in facts(method, "KotlinSourceMethod"))
assert slot["vis"] == "public" and slot["ret"] == {"t": "fqn", "name": "System.Object"}
assert [p["type"] for p in slot["params"]] == [physical_array]
carrier_calls = [node for node in objects(root) if node.get("k") == "callInstance"
                 and node.get("ownerType", {}).get("name") == carrier["name"]
                 and node.get("method") == slot["name"]]
assert len(carrier_calls) == 1
selected = carrier_calls[0]
assert selected["sig"] == [physical_array] and selected["ret"] == slot["ret"]
assert selected["memberRef"]["assembly"] == "RoundtripProducer"
assert selected["memberRef"]["declaringType"] == {"t": "fqn", "name": carrier["name"]}
assert selected["memberRef"]["parameterTypes"] == [physical_array]
assert selected["memberRef"]["returnType"] == slot["ret"]
assert any(node.get("k") == "cast" and node.get("type") == {"t": "array", "elem": {"t": "fqn", "name": "System.String"}}
           and node.get("e") is selected for node in objects(root))
print(
    "referenced generic-owner direct/open/callable/value access keeps exact physical ABI "
    "and closes nullable companion results"
)
