#!/usr/bin/env python3
"""Exact selected top-level identities govern deferred unchecked generic results."""
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


def load(path):
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


bir, reader, cir = map(load, sys.argv[1:])
source = {method["name"]: method for method in bir["methods"] if method["name"] != "overloaded"}
physical = {method["name"]: method for method in cir["methods"]}
readers = [method for method in reader["methods"] if method["name"] == "readOrdinaryTop"]
assert len(readers) == 1, readers
overloads = [method for method in bir["methods"] if method["name"] == "overloaded"]
assert len(overloads) == 2, overloads
erased_overload = next(method for method in overloads if method["params"][0]["type"]["t"] == "nullable")
typed_overload = next(method for method in overloads if method != erased_overload)
assert erased_overload["declarationId"] != typed_overload["declarationId"]

for caller, selected, owner, erased in (
    ("deferred", source["read"], bir["fileClass"], True),
    ("ordinary", readers[0], reader["fileClass"], True),
    ("deferredOverload", erased_overload, bir["fileClass"], True),
    ("ordinaryOverload", typed_overload, bir["fileClass"], False),
):
    declarations = [node for node in objects(source[caller]["body"]) if node.get("k") == "var"]
    assert len(declarations) == 1, (caller, declarations)
    call = declarations[0]["init"]
    assert call["declarationId"] == selected["declarationId"], (caller, call)
    if "calleeOwner" in call:
        assert call["calleeOwner"]["name"] == owner, (caller, call)
    if caller == "deferred":
        assert call["calleeOwner"]["name"] == owner, call
    lowered = [node for node in objects(physical[caller]["body"]) if node.get("k") == "var"]
    assert len(lowered) == 1, (caller, lowered)
    local = lowered[0]
    assert (local["type"].get("name") == "object") == erased, (caller, local)
    if erased:
        assert local["init"]["ret"] == {"t": "fqn", "name": "object"}, (caller, local)
    else:
        assert local["type"]["t"] == "tv", local

concrete_read = [node for node in objects(source["concrete"]["body"])
                 if node.get("k") == "callStatic" and node.get("method") == "read"]
assert len(concrete_read) == 1, concrete_read
assert concrete_read[0]["declarationId"] == source["read"]["declarationId"]
physical_read = [node for node in objects(physical["concrete"]["body"])
                 if node.get("k") == "callStatic" and node.get("method") == "read"]
assert len(physical_read) == 1, physical_read
assert physical["read"]["ret"] == {"t": "fqn", "name": "object"}, physical["read"]
assert physical_read[0]["ret"] == {"t": "fqn", "name": "System.Int32"}, physical_read
print("Private/cross-file selected identities, overload isolation, erased storage and concrete checks: OK")
