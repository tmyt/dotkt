#!/usr/bin/env python3
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
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} generic UnsafeAccessor holders, expected 1")

holder = holders[0]
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
owner_tv = {"t": "tv", "scope": "type", "i": 0}
base_open = {
    "t": "fqn",
    "name": "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase`1",
    "args": [owner_tv],
}
physical_array = {"t": "array", "elem": {"t": "fqn", "name": "System.Object"}}
expected_params = [base_open, physical_array]
if [parameter.get("type") for parameter in accessor.get("params", [])] != expected_params:
    raise SystemExit(f"UnsafeAccessor does not state the referenced MethodDef's physical parameters: {accessor!r}")
if accessor.get("ret") != physical_array:
    raise SystemExit(f"UnsafeAccessor does not state the referenced MethodDef's physical return: {accessor!r}")

closures = [
    item
    for item in root.get("types", [])
    if item.get("nestedIn") == "ReferencedProtectedGenericOwnerText"
]
if len(closures) != 1:
    raise SystemExit(f"found {len(closures)} lifted closures, expected 1")

casts = [
    node
    for node in objects(closures[0].get("methods", []))
    if node.get("k") == "cast"
    and node.get("e", {}).get("k") == "callStatic"
    and node.get("e", {}).get("owner", {}).get("name") == holder.get("name")
]
if len(casts) != 1:
    raise SystemExit(f"found {len(casts)} concrete projections from the holder call, expected 1")

projection = casts[0]
semantic_array = {"t": "array", "elem": {"t": "fqn", "name": "System.String"}}
call = projection["e"]
if projection.get("type") != semantic_array:
    raise SystemExit(f"holder result is not projected to the concrete Kotlin array: {projection!r}")
if call.get("owner", {}).get("args") != [{"t": "fqn", "name": "System.String"}]:
    raise SystemExit(f"holder call does not construct the referenced owner frame with String: {call!r}")
if call.get("sig") != expected_params or call.get("ret") != {
    "t": "array",
    "elem": {"t": "fqn", "name": "object"},
}:
    raise SystemExit(f"holder call and physical accessor declaration disagree: {call!r}")

print("referenced generic-owner UnsafeAccessor keeps its physical frame and concrete result projection")
