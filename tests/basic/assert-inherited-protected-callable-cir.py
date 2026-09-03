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
    raise SystemExit("usage: assert-inherited-protected-callable-cir.py <cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

def target_name(method):
    names = [
        argument.get("value", {}).get("value")
        for attribute in method.get("attrs", [])
        for argument in attribute.get("namedArgs", [])
        if argument.get("name") == "Name"
    ]
    return names[0] if len(names) == 1 else None

accessor_pairs = [
    (owner, method)
    for owner in root.get("types", [])
    for method in owner.get("methods", [])
    if method.get("extern") and target_name(method) is not None
]
targets = Counter(target_name(method) for _, method in accessor_pairs)
if targets != Counter({"echo": 1, "select": 1, "selectMutable": 1}):
    raise SystemExit(f"callable-reference UnsafeAccessors lost their selected physical targets: {targets!r}")

holder, accessor = next(pair for pair in accessor_pairs if target_name(pair[1]) == "echo")
holder_type_params = holder.get("typeParams", [])
holder_type_param_names = [parameter if isinstance(parameter, str) else parameter.get("name")
                           for parameter in holder_type_params]
if holder_type_param_names != ["__owner0"]:
    raise SystemExit(f"UnsafeAccessor holder lost the base owner's generic frame: {holder!r}")

base_open = {
    "t": "fqn",
    "name": "ProtectedNullableCallable",
    "args": [{"t": "tv", "scope": "type", "i": 0}],
}
physical_object = {"t": "fqn", "name": "object"}
expected_signature = [base_open, physical_object]
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

public_calls = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "callInstance" and node.get("method") == "echoPublic"
]
if len(public_calls) != 1:
    raise SystemExit(f"found {len(public_calls)} inherited public calls, expected 1")
public_call = public_calls[0]
expected_public_owner = {
    "t": "fqn",
    "name": "PublicNullableCallable",
    "args": [{"t": "fqn", "name": "System.Int32"}],
}
if public_call.get("ownerType") != expected_public_owner or public_call.get("sig") != [physical_object]:
    raise SystemExit(f"inherited public call disagrees with its local MethodDef owner/ABI: {public_call!r}")

print("inherited callable references use their local base MethodDef owner and physical ABI")
