#!/usr/bin/env python3
import base64
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

with open(sys.argv[1], encoding="utf-8") as stream:
    producer = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    root = json.load(stream)
storage = {"t": "fqn", "name": "System.Array"}
base_name = "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase`2"
calls = [node for node in objects(root) if node.get("k") == "callInstance"
         and node.get("ownerType", {}).get("name") == base_name]
assert Counter(call["method"] for call in calls) == Counter({"snapshot": 2, "openSnapshot": 1})
assert Counter(tuple(arg["name"] for arg in call["ownerType"]["args"]) for call in calls) == Counter({
    ("System.String", "System.String"): 2, ("System.Int32", "object"): 1})
for call in calls:
    assert call["sig"] == [storage] and call["ret"] == storage and len(call["args"]) == 1
    assert call["virtual"] == (call["method"] == "openSnapshot")
    assert call["memberRef"] == {"kind": "method", "assembly": "RoundtripProducer",
        "declaringType": call["ownerType"], "name": call["method"], "genericArity": 0,
        "returnType": storage, "callingConvention": "instance", "parameterTypes": [storage]}
    concrete = {"t": "array", "elem": call["ownerType"]["args"][1]}
    assert any(node.get("k") == "cast" and node.get("type") == concrete and node.get("e") is call
               for node in objects(root))
assert not any(owner.get("name", "").startswith("dotkt$unsafe$holder$") for owner in root["types"])

def facts(declaration, name):
    return [json.loads(base64.b64decode(attr["args"][1]["bytes"])) for attr in declaration.get("attrs", [])
            if attr.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices." + name + "Attribute"]

source_owner = "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase"
source = next(item for item in producer["types"] if item["name"] == source_owner)
assert source["typeParams"] == [
    {"name": "T", "constraints": [{"t": "tv", "scope": "type", "i": 1}]}, "$nullable0"]
original = next(method for method in source["methods"] if method["name"] == "snapshot")
assert original["ret"] == storage and [param["type"] for param in original["params"]] == [storage]
assert original["vis"] == "protected" and facts(original, "KotlinCallableVisibility") == [{"visibility": "protected"}]
identity = facts(original, "KotlinDeclarationIdentity")[0]["id"]
carrier = next(item for item in producer["types"]
               if any(fact.get("name") == source_owner for fact in facts(item, "KotlinType")))
slot = next(method for method in carrier["methods"]
            if {"name": "snapshot", "sourceDeclaration": identity} in facts(method, "KotlinSourceMethod"))
assert slot["vis"] == "public" and slot["ret"] == {"t": "fqn", "name": "System.Object"}
assert [p["type"] for p in slot["params"]] == [storage]
selected, = [node for node in objects(root) if node.get("k") == "callInstance"
             and node.get("ownerType", {}).get("name") == carrier["name"] and node.get("method") == slot["name"]]
assert selected["sig"] == [storage] and selected["ret"] == slot["ret"]
assert selected["memberRef"]["assembly"] == "RoundtripProducer"
assert selected["memberRef"]["declaringType"] == {"t": "fqn", "name": carrier["name"]}
assert selected["memberRef"]["parameterTypes"] == [storage]
assert selected["memberRef"]["returnType"] == slot["ret"]
assert any(node.get("k") == "cast" and node.get("type") == {"t": "array", "elem": {"t": "fqn", "name": "System.String"}}
           and node.get("e") is selected for node in objects(root))
print("referenced family direct/open/callable/value paths retain exact ABI, full owner frames and array projections")
