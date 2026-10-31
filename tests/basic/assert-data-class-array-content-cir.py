#!/usr/bin/env python3
"""Generated array operations keep the readable projection bound, not a consuming lower bound."""
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

for name, argument in (
    ("InArrayRecord", {"t": "fqn", "name": "System.Object"}),
    ("StarArrayRecord", {"t": "fqn", "name": "System.Object"}),
    ("GenericArrayRecord", {"t": "tv", "scope": "type", "i": 0}),
):
    declaration, = [node for node in root["types"] if node.get("name") == name]
    for method, helper in (("ToString", "contentToString"), ("GetHashCode", "contentHashCode")):
        body, = [node["body"] for node in declaration["methods"] if node.get("name") == method]
        call, = [node for node in objects(body) if node.get("method") == helper]
        assert call.get("typeArgs") == [argument], (name, method, call.get("typeArgs"))
        assert call["memberRef"]["genericArity"] == 1, (name, method)

print("Generated array content calls preserve readable projection bounds and source generic frames")
