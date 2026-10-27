#!/usr/bin/env python3
"""Physical inherited forwarders preserve CLR slots without replacing Kotlin source declarations."""
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


producer, source, consumer = [json.load(open(path, encoding="utf-8")) for path in sys.argv[1:]]
types = {item["name"]: item for item in producer["types"]}
identities = {}
slots = {}
for owner in ("genq.SlotBase", "genq.SlotConsumer"):
    declaration = next(method for method in types[owner]["methods"] if method["name"] == "accept")
    identities[owner] = payloads(declaration, "KotlinDeclarationIdentity")[0]["id"]
    candidates = [method for method in types[owner + "$star"]["methods"]
                  if any(fact.get("sourceDeclaration") == identities[owner]
                         for fact in payloads(method, "KotlinSourceMethod"))]
    assert len(candidates) == 1
    slots[owner] = candidates[0]
assert len(set(identities.values())) == 2
derived = types["genq.SlotDerived"]
forwarders = [method for method in derived["methods"] if method["name"] == "accept"]
assert len(forwarders) == 1 and forwarders[0]["generated"] is True
assert not payloads(forwarders[0], "KotlinDeclarationIdentity")
implementations = [edge for method in derived["methods"] for edge in method.get("clrInterfaceImpls", [])]
for owner, slot in slots.items():
    assert any(edge["owner"] == {"t": "fqn", "name": owner + "$star"}
               and edge["member"] == slot["name"]
               and edge["params"] == [parameter["type"] for parameter in slot["params"]]
               and edge["ret"] == slot["ret"] for edge in implementations)
assert any(edge["owner"] == {"t": "fqn", "name": "genq.SlotConsumer", "args": [
    {"t": "tv", "scope": "type", "i": index} for index in range(3)]} for edge in implementations)


def test_method(root):
    host = next(item for item in root["types"] if item["name"] == "GenericMetadataRoundtripTests")
    return next(method for method in host["methods"] if method["name"] == "nullableGenericMembersRoundTrip")


source_method = test_method(source)
lowered_method = test_method(consumer)
for owner, count in (("genq.SlotBase", 5), ("genq.SlotConsumer", 2)):
    selected = [node for node in nodes(source_method) if node.get("k") == "callInstance"
                and node.get("declarationId") == identities[owner]]
    assert len(selected) == count
    calls = [node for node in nodes(lowered_method) if node.get("k") == "callInstance"
             and node.get("ownerType", {}).get("name") == owner + "$star"
             and node.get("method") == slots[owner]["name"]]
    assert len(calls) == count
    assert all(call["memberRef"]["declaringType"] == call["ownerType"] for call in calls)
assert not any(node.get("ownerType", {}).get("name") == "genq.SlotDerived$star"
               and node.get("method") == "accept" for node in nodes(lowered_method))
print("physical inherited forwarders preserve source declaration IDs and every exact CLR implementation slot")
