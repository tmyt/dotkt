#!/usr/bin/env python3
"""Check the physical array ABI and concrete projections on legal direct family edges."""
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
types = {owner["name"]: owner for owner in root["types"]}
string = {"t": "fqn", "name": "System.String"}
array = {"t": "array", "elem": string}
storage = {"t": "fqn", "name": "System.Array"}
base = {"t": "fqn", "name": "NgProtectedArrayBase", "args": [string, string]}
declaration = types["NgProtectedArrayBase"]
assert len(declaration["typeParams"]) == 2
assert declaration["typeParams"][0]["constraints"] == [{"t": "tv", "scope": "type", "i": 1}]
getter, = [method for method in declaration["methods"] if method["name"] == "prop_get<values>"]
assert getter["ret"] == storage and getter["params"] == []
snapshot, = [method for method in types["NgProtectedArrayText"]["methods"] if method["name"] == "snapshot"]
call, = [node for node in objects(snapshot["body"]) if node.get("k") == "callInstance"
         and node.get("method") == "prop_get<values>"]
assert call["ownerType"] == base and call["sig"] == [] and call["calleeRet"] == storage
assert any(node.get("k") == "cast" and node.get("type") == array and node.get("e") is call
           for node in objects(snapshot["body"]))
captured = [node for node in objects(root) if node.get("k") == "setField" and node.get("name") == "v"
            and node.get("value", {}).get("k") == "cast" and node["value"].get("type") == array
            and node["value"].get("e", {}).get("k") == "callInstance"
            and node["value"]["e"].get("ownerType") == base
            and node["value"]["e"].get("calleeRet") == storage]
assert len(captured) == 1, "Captured property must close its companion before the ref-cell store"
closure, = [owner for owner in root["types"] if owner.get("nestedIn") == "NgProtectedMethodText"]
target, = [method for method in types["NgProtectedMethodBase"]["methods"] if method["name"] == "pick"]
assert len(target["typeParams"]) == 2
assert target["typeParams"][0]["constraints"] == [{"t": "tv", "scope": "method", "i": 1}]
assert target["ret"] == storage and [param["type"] for param in target["params"]] == [storage]
method_call, = [node for node in objects(closure["methods"]) if node.get("k") == "callInstance"
                and node.get("method") == "pick"]
assert method_call["ownerType"] == {"t": "fqn", "name": "NgProtectedMethodBase"}
assert method_call["sig"] == [storage] and method_call["typeArgs"] == [string, string]
assert method_call["ret"] == method_call["calleeRet"] == storage
assert any(node.get("k") == "setField" and node.get("name") == "v"
           and node.get("value", {}).get("k") == "cast" and node["value"].get("type") == array
           and node["value"].get("e") is method_call for node in objects(closure["methods"]))
print("direct nullable-array family calls retain storage ABI, complete frames and concrete result casts")
