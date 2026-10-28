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

physical_object = {"t": "fqn", "name": "object"}
closure_calls = [node for owner in root["types"] if owner.get("nestedIn")
                 for node in objects(owner.get("methods", [])) if node.get("k") == "callInstance"
                 and node.get("method") in {"echo", "select", "selectMutable"}]
if Counter(call["method"] for call in closure_calls) != Counter({"echo": 1, "select": 1, "selectMutable": 1}):
    raise SystemExit(f"callable closures lost their exact direct targets: {closure_calls!r}")
call = next(call for call in closure_calls if call["method"] == "echo")
if (call.get("ownerType") != {"t": "fqn", "name": "ProtectedNullableCallable", "args": [{"t": "fqn", "name": "System.Int32"}]}
        or call.get("sig") != [physical_object] or call.get("ret") != physical_object
        or call.get("calleeRet") != physical_object or call.get("virtual") is not True):
    raise SystemExit(f"direct callable lost selected constructed owner/physical ABI: {call!r}")
arguments = call.get("args", [])
if len(arguments) != 1 or arguments[0].get("k") != "cast" or arguments[0].get("type") != physical_object:
    raise SystemExit(f"nullable-generic argument was not projected to the physical object slot: {call!r}")
for call in closure_calls:
    if call["method"] == "echo":
        continue
    if (call.get("ownerType") != {"t": "fqn", "name": "ProtectedErasedOverloadCallable"}
            or call.get("sig") != [{"t": "fqn", "name": "System.Object"}]
            or call.get("ret") != {"t": "fqn", "name": "System.Int32"}
            or call.get("calleeRet") != call["ret"] or call.get("virtual") is not True):
        raise SystemExit(f"overloaded callable lost selected physical declaration: {call!r}")

public_calls = [
    node
    for owner in root.get("types", []) if owner.get("name") != "PublicNullableCallable"
    for node in objects(owner.get("methods", []))
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
public_owner = next(owner for owner in root["types"] if owner.get("name") == "PublicNullableCallable")
carrier_bridges = [method for method in public_owner["methods"]
                   if any(slot.get("owner") == {"t": "fqn", "name": "PublicNullableCallable$star"}
                          and slot.get("params") == [physical_object]
                          for slot in method.get("clrInterfaceImpls", []))]
if len(carrier_bridges) != 1:
    raise SystemExit(f"public member lost its exact carrier slot: {carrier_bridges!r}")
bridge_calls = [node for node in objects(carrier_bridges[0]["body"])
                if node.get("k") == "callInstance" and node.get("method") == "echoPublic"]
if len(bridge_calls) != 1 or bridge_calls[0].get("ownerType") != {
        "t": "fqn", "name": "PublicNullableCallable", "args": [{"t": "tv", "scope": "type", "i": 0}]
} or bridge_calls[0].get("sig") != [physical_object]:
    raise SystemExit(f"public carrier bridge lost its selected declaration: {bridge_calls!r}")

print("inherited callable references use their local base MethodDef owner and physical ABI")
