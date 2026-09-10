#!/usr/bin/env python3
"""Check suspend source constraints, carrier identity, and cold-entry frame ownership."""
import base64
import json
import sys


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
    return json.loads(base64.b64decode(matches[0]["args"][1]["bytes"]))


with open(sys.argv[1], encoding="utf-8") as stream:
    types = {t["name"]: t for t in json.load(stream)["types"]}

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
constructions = [n for n in objects(cold["body"]) if n.get("k") == "new"
                 and n.get("type", {}).get("name") == "OwnerSuspendSink_render$sm"]
assert len(constructions) == 1, constructions
assert constructions[0]["type"]["args"] == [
    {"t": "tv", "scope": "type", "i": 0},
    {"t": "tv", "scope": "method", "i": 0},
], constructions[0]["type"]

slots = types["OwnerSuspendSink$star"]["methods"]
for name in ("render", "echo"):
    task_slots = [m for m in slots if any(
        a["attr"].get("name") == "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute"
        for a in m.get("attrs", []))
        and payload(m, "KotlinSourceMethodAttribute") == {"name": name}]
    assert len(task_slots) == 1, (name, task_slots)
    task = task_slots[0]
    cold = next(m for m in slots if m["name"] == task["name"] + "$dotkt_suspend")
    assert task["typeParams"] == cold["typeParams"], name
    assert len(cold["params"]) == len(task["params"]) + 1, name
    logical_result = payload(task, "KotlinSuspendResultAttribute")
    assert logical_result == ({"t": "fqn", "name": "kotlin.String"} if name == "render"
                              else {"t": "tv", "scope": "method", "i": 0}), logical_result

print("owner-constrained suspend metadata, physical bounds and construction frames are exact")
