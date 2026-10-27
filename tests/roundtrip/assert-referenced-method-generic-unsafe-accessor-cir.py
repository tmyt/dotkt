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

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    producer = json.load(stream)
source, = [owner for owner in producer["types"]
           if owner["name"] == "roundtrip.protectedmethodgeneric.ReferencedProtectedMethodGenericBase"]
target, = [method for method in source["methods"] if method["name"] == "snapshot"]
assert len(target["typeParams"]) == 2
assert target["typeParams"][0]["constraints"] == [{"t": "tv", "scope": "method", "i": 1}]
closure, = [owner for owner in root["types"] if owner.get("nestedIn") == "ReferencedProtectedMethodGenericText"]
base = {"t": "fqn", "name": "roundtrip.protectedmethodgeneric.ReferencedProtectedMethodGenericBase"}
storage = {"t": "fqn", "name": "System.Array"}
assert target["ret"] == storage and [param["type"] for param in target["params"]] == [storage]
string = {"t": "fqn", "name": "System.String"}
call, = [node for node in objects(closure["methods"]) if node.get("k") == "callInstance"
         and node.get("method") == "snapshot"]
assert call["ownerType"] == base and call["typeArgs"] == [string, string]
assert call["sig"] == [storage] and len(call["args"]) == 1 and call["virtual"] is False
member = call["memberRef"]
assert member == {"kind": "method", "assembly": "RoundtripProducer", "declaringType": base,
                  "name": "snapshot", "genericArity": 2, "returnType": storage,
                  "callingConvention": "instance", "parameterTypes": [storage]}
assert any(node.get("k") == "cast" and node.get("type") == {"t": "array", "elem": string}
           and node.get("e") is call for node in objects(closure["methods"]))
assert not any(method.get("extern") and method.get("name", "").startswith("dotkt$unsafe$")
               for method in closure["methods"])
print("referenced direct method-generic family call retains exact member, full frame and array projection")
