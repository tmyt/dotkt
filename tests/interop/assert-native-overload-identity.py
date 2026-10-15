#!/usr/bin/env python3
"""Keep frontend declaration identity distinct from a constructed native owner's coincident signatures."""
import json
import sys


def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)


def bare(value):
    while value.get("t") in ("oblivious", "nullable"):
        value = value["of"]
    return value


def fqn(name):
    return {"t": "fqn", "name": name}


owners = {"NativeOverloadIdentity.ISlot`1", "NativeOverloadIdentity.IReverseSlot`1"}
bir_path, cir_path = sys.argv[1:]
with open(bir_path, encoding="utf-8") as stream:
    bir = json.load(stream)
with open(cir_path, encoding="utf-8") as stream:
    cir = json.load(stream)

bir_calls = [node for node in nodes(bir) if node.get("k") == "callInstance"
             and node.get("ownerType", {}).get("name") in owners
             and node.get("method") in ("Echo", "Plain", "Optional")]
assert bir_calls, "native calls disappeared from BIR"
seen = set()
open_controls = 0
for call in bir_calls:
    signature = [bare(item) for item in call.get("shapeTypes", call.get("sig", []))]
    assert signature, call
    owner = call["ownerType"]
    if owner.get("args") == [fqn("kotlin.Int")]:
        assert signature[0] == fqn("kotlin.Int"), ("lost selected declaration", call)
        result = call.get("sty", call.get("ret"))
        if result is not None:
            assert bare(result) == fqn("kotlin.String"), ("selected result disagrees", call)
        seen.add((owner["name"], call["method"]))
    elif signature[0] == {"t": "tv", "scope": "type", "i": 0}:
        open_controls += 1
assert open_controls >= 2, "generic owner call/reference controls disappeared"
assert seen == {(owner, method) for owner in owners for method in ("Echo", "Plain", "Optional")}, seen

cir_calls = [node for node in nodes(cir) if node.get("memberRef", {}).get("declaringType", {}).get("name") in owners
             and node["memberRef"].get("name") in ("Echo", "Plain", "Optional")]
assert cir_calls, "native operands disappeared from CIR"
closed_count = 0
for call in cir_calls:
    member = call["memberRef"]
    assert "resolvedMemberParams" not in call, ("unconsumed declaration vector", call)
    assert member["callingConvention"] == "instance", member
    if member["declaringType"].get("args") == [fqn("System.Int32")]:
        assert member["parameterTypes"][0] == fqn("System.Int32"), member
        assert member["returnType"] == fqn("System.String"), member
        assert member["genericArity"] == (0 if member["name"] == "Plain" else 1), member
        closed_count += 1
assert closed_count >= 15, ("direct, inherited, reversed or adapter operands disappeared", closed_count)

fixture = next(owner for owner in cir["types"] if owner["name"] == "NativeOverloadIdentityTests")
original = next(method for method in fixture["methods"]
                if method["name"] == "originalTwoHopCollisionKeepsTheSelectedDeclaration")
original_calls = [node for node in nodes(original) if node.get("memberRef", {}).get("name") == "Echo"]
assert len(original_calls) == 1, original_calls
assert original_calls[0]["memberRef"]["returnType"] == fqn("System.String"), original_calls

for owner_name, member_name, minimum in (
    ("NativeOverloadIdentity.ClassSlot`1", "Echo", 3),
    ("NativeOverloadIdentity.IIndexedSlot`1", "get_Item", 2),
    ("NativeOverloadIdentity.IReverseIndexedSlot`1", "get_Item", 1),
):
    matches = [node["memberRef"] for node in nodes(cir)
               if node.get("memberRef", {}).get("declaringType", {}).get("name") == owner_name
               and node["memberRef"].get("name") == member_name]
    assert len(matches) >= minimum, (owner_name, matches)
    for member in matches:
        assert member["parameterTypes"][0] == fqn("System.Int32"), member
        assert member["returnType"] == fqn("System.String"), member

awaiters = [node["memberRef"] for node in nodes(cir)
            if node.get("memberRef", {}).get("name") == "GetAwaiter"]
assert len(awaiters) >= 3, awaiters
assert all(member["declaringType"]["name"] == "System.Threading.Tasks.Task`1"
           and member["returnType"]["name"] == "System.Runtime.CompilerServices.TaskAwaiter`1"
           for member in awaiters), awaiters
completions = [node["memberRef"] for node in nodes(cir)
               if node.get("memberRef", {}).get("name") in ("Mirror", "GenericMirror")]
assert len(completions) >= 5, completions
for member in completions:
    assert member["declaringType"] == {
        "t": "fqn", "name": "NativeOverloadIdentity.ICompletedSlot`1", "args": [fqn("System.String")]
    }, member
    assert member["parameterTypes"][0] == member["returnType"] == {
        "t": "tv", "scope": "type", "i": 0
    }, member
print("native overload identities and result signatures survive owner substitution and adapters")
