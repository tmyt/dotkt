#!/usr/bin/env python3
"""A referenced generic result cannot inherit a same-named local's erased ABI."""
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
for caller, selected, erased in (("local", source["read"], True), ("referenced", external, False)):
    declaration = next(node for node in objects(source[caller]["body"]) if node.get("k") == "var")
    assert declaration["init"]["declarationId"] == selected["declarationId"], declaration
    lowered = next(node for node in objects(physical[caller]["body"]) if node.get("k") == "var")
    assert (lowered["type"].get("name") == "object") == erased, lowered
    if not erased:
        assert lowered["type"]["t"] == "tv", lowered
print("Selected referenced identity stays isolated from a same-named local erased producer: OK")
