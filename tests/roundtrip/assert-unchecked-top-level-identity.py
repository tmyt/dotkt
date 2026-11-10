#!/usr/bin/env python3
"""Opaque generic results must retain their selected local or referenced declaration."""
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


documents = []
for path in sys.argv[1:]:
    with open(path, encoding="utf-8") as stream:
        documents.append(json.load(stream))
producer, bir, cir = documents
source = {method["name"]: method for method in bir["methods"]}
physical = {method["name"]: method for method in cir["methods"]}
external = next(method for method in producer["methods"] if method["name"] == "read")
assert external["declarationId"] != source["read"]["declarationId"]
for caller, selected in (("local", source["read"]), ("referenced", external)):
    declaration = next(node for node in objects(source[caller]["body"]) if node.get("k") == "var")
    assert declaration["init"]["declarationId"] == selected["declarationId"], declaration
    lowered = next(node for node in objects(physical[caller]["body"]) if node.get("k") == "var")
    assert lowered["type"].get("name") in ("object", "System.Object"), lowered
    calls = [node for node in objects(lowered["init"])
             if node.get("k") == "callStatic" and node.get("method") == "read"]
    assert len(calls) == 1, lowered
    call = calls[0]
    if caller == "referenced":
        member = call["memberRef"]
        assert member["assembly"] == "RoundtripProducer", member
        assert member["declaringType"]["name"] == producer["fileClass"], member
        assert member["returnType"].get("name") in ("object", "System.Object"), member
    else:
        assert call.get("owner") is None and call.get("memberRef") is None, call
        assert call["calleeOwner"]["name"] == cir["fileClass"], call
        assert physical["read"]["ret"].get("name") in ("object", "System.Object"), physical["read"]
print("Opaque local and referenced results retain their distinct selected declarations: OK")
