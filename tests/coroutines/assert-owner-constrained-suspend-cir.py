#!/usr/bin/env python3
"""Check suspend source constraints, carrier identity, and cold-entry frame ownership."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "roundtrip"))
from carrier_inspection import metadata_record, type_payloads


def objects(node):
    if isinstance(node, dict):
        yield node
        for child in node.values():
            yield from objects(child)
    elif isinstance(node, list):
        for child in node:
            yield from objects(child)


def payload(method, short_name):
    matches = [a for a in method.get("attrs", [])
               if a["attr"].get("name") == "DotKt.Runtime.CompilerServices." + short_name]
    assert len(matches) == 1, (method["name"], short_name, len(matches))
    return decoded_types[id(matches[0])] if short_name == "KotlinSuspendResultAttribute" else \
        metadata_record(matches[0]["args"], sys.argv[2])


if len(sys.argv) != 3:
    raise SystemExit("usage: assert-owner-constrained-suspend-cir.py <cir.json> <metadata-inspector.dll>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)
    types = {t["name"]: t for t in root["types"]}

decoded_types = type_payloads([node for node in objects(root) if node.get("attr", {}).get("name")
                             == "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute"], sys.argv[2])

assert not any("_ownerConstraintDispatchBounds" in node or "erasedInnerConstraints" in node
               for node in objects(list(types.values())))

for owner_name, source_bound in (
    ("OwnerSuspendSink", {"t": "tv", "scope": "type", "i": 0}),
    ("OwnerSuspendAnimalSink", {"t": "fqn", "name": "OwnerSuspendAnimal"}),
):
    methods = {m["name"]: m for m in types[owner_name]["methods"]}
    for name in ("render", "render$dotkt_suspend"):
        assert methods[name]["typeParams"][0].get("constraints", []) == [], (owner_name, name)
    assert payload(methods["render"], "KotlinTypeParameterBoundsAttribute") == {
        "bounds": {"0": [source_bound]}
    }, owner_name

owner = types["OwnerSuspendSink"]
cold = next(m for m in owner["methods"] if m["name"] == "render$dotkt_suspend")
assert len(cold["typeParams"]) == 2 and cold["params"][0]["type"] == {"t": "fqn", "name": "object"}, cold


def method_arg(index):
    return {"t": "tv", "scope": "method", "i": index}


# The ordinary cold slot forwards to the selected projected implementation.
# Verify both frame edges; the generated state's incidental name is not an ABI.
forward, = [n for n in objects(cold["body"]) if n.get("k") == "callInstance"]
owner_arg = {"t": "tv", "scope": "type", "i": 0}
assert forward["virtual"] and forward["recv"] == {"k": "this"}
assert forward["ownerType"] == {"t": "fqn", "name": owner["name"], "args": [owner_arg]}
assert forward["typeArgs"] == [method_arg(0), method_arg(1), method_arg(0),
                               {"t": "fqn", "name": "System.Object"}, method_arg(0), method_arg(0)]
assert forward["args"] == [{"k": "local", "name": p["name"]} for p in cold["params"]]
implementation, = [m for m in owner["methods"] if m["name"] == forward["method"]]
resume, = [n for n in objects(implementation["body"])
           if n.get("k") == "callInstance" and n["method"] == "invokeSuspend"]
state = resume["ownerType"]
construction, = [n for n in objects(implementation["body"]) if n.get("k") == "new" and n["type"] == state]
assert state["args"] == [owner_arg, *(method_arg(i) for i in range(4))], state
assert types[state["name"]]["base"]["name"] == "kotlin.coroutines.clr.internal.ContinuationImpl"
assert construction["args"] == [{"k": "this"}, *forward["args"]]

slots = types["OwnerSuspendSink$star"]["methods"]
for name in ("render", "echo"):
    source_identity = payload(next(m for m in owner["methods"] if m["name"] == name),
                              "KotlinDeclarationIdentityAttribute")["id"]
    task_slots = [m for m in slots if any(
        a["attr"].get("name") == "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute"
        for a in m.get("attrs", []))
        and payload(m, "KotlinSourceMethodAttribute")
        == {"name": name, "sourceDeclaration": source_identity}]
    assert len(task_slots) == 1, (name, task_slots)
    task = task_slots[0]
    cold = next(m for m in slots if m["name"] == task["name"] + "$dotkt_suspend")
    assert task["typeParams"] == cold["typeParams"], name
    assert len(cold["params"]) == len(task["params"]) + 1, name
    logical_result = payload(task, "KotlinSuspendResultAttribute")
    assert logical_result == ({"t": "fqn", "name": "kotlin.String"} if name == "render"
                              else {"t": "tv", "scope": "method", "i": 0}), logical_result

print("owner-constrained suspend metadata, physical bounds and construction frames are exact")
