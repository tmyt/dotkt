#!/usr/bin/env python3
"""Pin selected source-default identities and their exact inherited native MethodImpl obligations."""
import base64
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


def carriers(method, name):
    return [json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
            for attribute in method.get("attrs", [])
            if attribute.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


with open(sys.argv[1], encoding="utf-8") as stream:
    source = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    producer = json.load(stream)
with open(sys.argv[3], encoding="utf-8") as stream:
    consumer = json.load(stream)
owner = "roundtrip.defaultpropertyslot.ReferencedGenericRenamedDefaultMethodSlot"
source_type = next(item for item in source["types"] if item["name"] == owner)
selected = next(method for method in source_type["methods"]
                if method.get("params", [{}])[0].get("type", {}).get("name") == owner)
overload = next(method for method in source_type["methods"]
                if method.get("params", [{}])[0].get("type", {}).get("name")
                == "roundtrip.defaultpropertyslot.ReferencedCompareMarker")
assert selected["declarationId"] != overload["declarationId"]
physical_type = next(item for item in producer["types"] if item["name"] == owner)
body = next(method for method in physical_type["methods"]
            if any(identity.get("id") == selected["declarationId"]
                   for identity in carriers(method, "KotlinDeclarationIdentity")))
bridges = [method for method in physical_type["methods"]
           if any(identity.get("sourceDeclaration") == selected["declarationId"]
                  for identity in carriers(method, "KotlinSourceMethod"))]
assert len(bridges) == 1
assert not any(identity.get("sourceDeclaration") == overload["declarationId"]
               for method in physical_type["methods"] for identity in carriers(method, "KotlinSourceMethod"))
carrier = {"t": "fqn", "name": owner + "$star"}
native = {"t": "fqn", "name": "System.IComparable`1", "args": [carrier]}
obligations = [slot for slot in bridges[0]["clrInterfaceImpls"] if slot.get("owner") == native]
assert len(obligations) == 1
implementation = next(item for item in consumer["types"] if item["name"] == "ImportedGenericDefaultCollision")
forwarders = [method for method in implementation["methods"]
              if obligations[0] in method.get("clrInterfaceImpls", [])]
assert len(forwarders) == 1
calls = [node for node in objects(forwarders[0]["body"]) if node.get("k") == "callInstance"]
exact_owner = {"t": "fqn", "name": owner + "`2", "args": [
    {"t": "fqn", "name": "System.Object"}, {"t": "fqn", "name": "System.Object"}]}
assert physical_type["typeParams"] == ["T", "$storage0"]
assert len(calls) == 1 and calls[0]["ownerType"] == exact_owner
assert calls[0]["method"] == body["name"] and calls[0].get("virtual") is True
assert calls[0]["sig"] == [carrier] and calls[0]["ret"] == {"t": "fqn", "name": "System.Int32"}
source_carrier = {"t": "fqn", "name": "NativeDefaultListSource$star"}
exact_view = {"t": "fqn", "name": "System.IComparable`1", "args": [source_carrier]}
hints = [node for node in objects(consumer) if node.get("k") == "classRef"
         and node.get("type") == exact_view]
assert len(hints) == 1
source_interface = next(item for item in consumer["types"] if item["name"] == "NativeDefaultListSource")
assert exact_view in source_interface["interfaces"]
native_calls = [node for node in objects(consumer) if node.get("memberRef", {}).get("name") == "CompareTo"
                and node["memberRef"].get("declaringType", {}).get("name") == "GenericValueInterop.NativeTextComparisonBase"]
assert len(native_calls) == 1
assert native_calls[0]["memberRef"]["parameterTypes"] == [{"t": "fqn", "name": "System.String"}]
print("referenced inherited defaults preserve exact source IDs and every selected native MethodImpl slot")
