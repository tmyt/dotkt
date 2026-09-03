#!/usr/bin/env python3
import json
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


if len(sys.argv) != 2:
    raise SystemExit(
        "usage: assert-referenced-generic-owner-unsafe-accessor-cir.py "
        "<ReferencedProtectedGenericOwnerTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

holders = [
    item
    for item in root.get("types", [])
    if item.get("generated") and item.get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(holders) != 4:
    raise SystemExit(f"found {len(holders)} generic UnsafeAccessor holders, expected 4")

owner_tv = {"t": "tv", "scope": "type", "i": 0}
base_open = {
    "t": "fqn",
    "name": "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase`1",
    "args": [owner_tv],
}
physical_array = {"t": "array", "elem": {"t": "fqn", "name": "System.Object"}}
expected_params = [base_open, physical_array]

accessor_targets = []
for holder in holders:
    if holder.get("typeParams") != [{"name": "__owner0"}]:
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
    names = [
        argument.get("value", {}).get("value")
        for attribute in accessor.get("attrs", [])
        for argument in attribute.get("namedArgs", [])
        if argument.get("name") == "Name"
    ]
    if len(names) != 1:
        raise SystemExit(f"UnsafeAccessor has no exact target name: {accessor!r}")
    accessor_targets.append(names[0])

if Counter(accessor_targets) != Counter({"snapshot": 3, "openSnapshot": 1}):
    raise SystemExit(f"final/open protected target set is incomplete: {accessor_targets!r}")

holder_names = {holder.get("name") for holder in holders}
wrapper_calls = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name") in holder_names
    and node.get("method", "").endswith("$invoke")
]
if len(wrapper_calls) != 4:
    raise SystemExit(f"found {len(wrapper_calls)} UnsafeAccessor wrapper calls, expected 4")

constructed_frames = Counter(
    tuple(argument.get("name") for argument in call.get("owner", {}).get("args", []))
    for call in wrapper_calls
)
if constructed_frames != Counter({("System.String",): 3, ("System.Int32",): 1}):
    raise SystemExit(f"holder calls use the wrong referenced owner frames: {constructed_frames!r}")

physical_call_return = {"t": "array", "elem": {"t": "fqn", "name": "object"}}
for call in wrapper_calls:
    if call.get("sig") != expected_params or call.get("ret") != physical_call_return:
        raise SystemExit(f"holder call and physical accessor declaration disagree: {call!r}")

casts = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "cast"
    and node.get("e", {}).get("k") == "callStatic"
    and node.get("e", {}).get("owner", {}).get("name") in holder_names
]
if len(casts) != 3:
    raise SystemExit(f"found {len(casts)} concrete projections from holder calls, expected 3")

semantic_array = {"t": "array", "elem": {"t": "fqn", "name": "System.String"}}
for projection in casts:
    call = projection["e"]
    if projection.get("type") != semantic_array:
        raise SystemExit(f"holder result is not projected to the concrete Kotlin array: {projection!r}")
    if call.get("owner", {}).get("args") != [{"t": "fqn", "name": "System.String"}]:
        raise SystemExit(f"holder call does not construct the referenced owner frame with String: {call!r}")
print(
    "referenced generic-owner direct/open/callable/value access keeps exact physical ABI "
    "and use projections"
)
