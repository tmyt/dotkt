#!/usr/bin/env python3
"""An ordinary mutable declaration remains the target of its branch assignments."""
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


with open(sys.argv[1], encoding="utf-8") as stream:
    bir = json.load(stream)
method = next(method for method in bir["methods"] if method["name"] == "ordinaryMutableUnit")
nodes = list(objects(method["body"]))
declarations = [node for node in nodes if node.get("k") == "var"]
assignments = [node for node in nodes if node.get("k") == "setLocal"]
assert len(declarations) == 1, declarations
assert len(assignments) == 2, assignments
assert all(node["name"] == declarations[0]["name"] for node in assignments), (declarations, assignments)
assert declarations[0]["init"]["value"] == 0, declarations
print("Ordinary local declarations retain their identity across branch assignments")
