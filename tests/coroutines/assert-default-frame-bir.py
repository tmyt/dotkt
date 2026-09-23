#!/usr/bin/env python3
"""A default closure must not substitute its caller's bounds as callee-owned types."""
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
    bir = json.load(stream)
owners = [node for node in objects(bir)
          if node.get("name") == "localsuspenddefaultframe.LocalDefaultOwner" and "methods" in node]
assert len(owners) == 1, owners
forward = next(method for method in owners[0]["methods"] if method["name"] == "forward")
closures = [node for node in objects(forward["body"]) if node.get("k") == "newClosure"]
assert len(closures) == 1, closures
closure = closures[0]
assert closure["typeArgs"] == [
    {"t": "tv", "scope": "method", "i": 0},
    {"t": "tv", "scope": "type", "i": 0},
], closure
parameters = closure["synthClass"]["typeParams"]
assert len(parameters) == 2 and parameters[1] == "T", parameters
assert parameters[0] == forward["typeParams"][0], (parameters, forward["typeParams"])
bound = parameters[0]["constraints"][0]
assert bound["name"].endswith("LocalDefaultBound"), bound
assert bound["args"] == [{"t": "tv", "scope": "type", "i": 0}], bound
print("Default closure preserves R : LocalDefaultBound<caller T> and construction <R,T>")
