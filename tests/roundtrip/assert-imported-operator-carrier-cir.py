#!/usr/bin/env python3
"""Keep selected delegated operators on their carrier without changing exact lexical super calls."""
import base64
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


def payloads(method, name):
    return [json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
            for attribute in method.get("attrs", [])
            if attribute.get("attr", {}).get("name")
            == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


with open(sys.argv[1], encoding="utf-8") as stream:
    producer = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    source = json.load(stream)
with open(sys.argv[3], encoding="utf-8") as stream:
    consumer = json.load(stream)

owner = "roundtrip.operatorresults.MutationListBase"
physical = next(item for item in producer["types"] if item["name"] == owner)
source_method = next(method for method in physical["methods"]
                     if any(identity.get("name") == "get" for identity in payloads(method, "KotlinDeclarationIdentity")))
identity = payloads(source_method, "KotlinDeclarationIdentity")[0]["id"]
carrier = next(item for item in producer["types"] if item["name"] == owner + "$star")
slots = [method for method in carrier["methods"]
         if any(association.get("sourceDeclaration") == identity
                for association in payloads(method, "KotlinSourceMethod"))]
assert len(slots) == 1
slot = slots[0]
assert payloads(slot, "KotlinSourceMethod") == [{"name": "get", "sourceDeclaration": identity}]
assert [parameter["type"] for parameter in slot["params"]] == [{"t": "fqn", "name": "System.Int32"}]
assert slot["ret"] == {"t": "fqn", "name": "System.Object"}
selected = next(method for method in source["methods"] if method["name"] == "checkImportedSet")
assert len([node for node in nodes(selected) if node.get("declarationId") == identity]) == 2
lowered = next(method for method in consumer["methods"] if method["name"] == selected["name"])
calls = [node for node in nodes(lowered) if node.get("k") == "callInstance" and node.get("method") == slot["name"]]
assert len(calls) == 2
for call in calls:
    assert call["ownerType"] == {"t": "fqn", "name": carrier["name"]}
    assert call["memberRef"]["declaringType"] == call["ownerType"]
    assert call["memberRef"]["returnType"] == slot["ret"] == call["ret"]
    assert not any(node.get("t") == "tv" and node.get("scope") == "type" for node in nodes(call))

derived = next(item for item in consumer["types"] if item["name"] == "roundtriptests.operatorresults.ReturningList")
super_calls = [node for node in nodes(derived["methods"])
               if node.get("k") == "callInstance" and node.get("method") == "set_Item"
               and node.get("ownerType", {}).get("name") == owner + "`2"]
assert len(super_calls) == 1
assert super_calls[0]["ownerType"]["args"] == [
    {"t": "tv", "scope": "type", "i": 0}, {"t": "tv", "scope": "type", "i": 1}]
assert super_calls[0].get("virtual") is False
print("selected imported operators use their exact associated carrier slots; lexical super frames remain exact")
