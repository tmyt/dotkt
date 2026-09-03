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
    raise SystemExit("usage: assert-inherited-protected-callable-cir.py <cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

holders = [
    item
    for item in root.get("types", [])
    if item.get("generated") and item.get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} UnsafeAccessor holders, expected 1")

holder = holders[0]
if holder.get("typeParams") != ["__owner0"]:
    raise SystemExit(f"UnsafeAccessor holder lost the base owner's generic frame: {holder!r}")

base_open = {
    "t": "fqn",
    "name": "ProtectedNullableCallable",
    "args": [{"t": "tv", "scope": "type", "i": 0}],
}
physical_object = {"t": "fqn", "name": "object"}
expected_signature = [base_open, physical_object]
accessors = [method for method in holder.get("methods", []) if method.get("extern")]
if len(accessors) != 1:
    raise SystemExit(f"found {len(accessors)} UnsafeAccessor declarations, expected 1")
accessor = accessors[0]
if [parameter.get("type") for parameter in accessor.get("params", [])] != expected_signature:
    raise SystemExit(f"UnsafeAccessor did not copy the local MethodDef's physical signature: {accessor!r}")
if accessor.get("ret") != physical_object:
    raise SystemExit(f"UnsafeAccessor did not copy the local MethodDef's physical return: {accessor!r}")

wrapper_calls = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name") == holder.get("name")
    and node.get("method", "").endswith("$invoke")
]
if len(wrapper_calls) != 1:
    raise SystemExit(f"found {len(wrapper_calls)} UnsafeAccessor wrapper calls, expected 1")
call = wrapper_calls[0]
if call.get("owner", {}).get("args") != [{"t": "fqn", "name": "System.Int32"}]:
    raise SystemExit(f"wrapper call did not construct the inherited base owner with Int: {call!r}")
if call.get("sig") != expected_signature or call.get("ret") != physical_object:
    raise SystemExit(f"wrapper call disagrees with the selected local MethodDef ABI: {call!r}")
arguments = call.get("args", [])
if len(arguments) != 2 or arguments[1].get("k") != "cast" or arguments[1].get("type") != physical_object:
    raise SystemExit(f"nullable-generic argument was not projected to the physical object slot: {call!r}")

print("inherited protected callable reference uses its local base MethodDef owner and physical ABI")
