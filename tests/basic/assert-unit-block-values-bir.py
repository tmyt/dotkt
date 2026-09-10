#!/usr/bin/env python3
"""Unit block results retain their ordered statements before any physical lowering."""
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
method = next(method for method in bir["methods"] if method["name"] == "unitBlockDeclaration")
conditional = method["body"][0]["value"]
assert conditional["k"] == "cond", conditional
unit = {"t": "fqn", "name": "kotlin.Unit"}
for side, kinds, tags in [("then", ["exprStmt", "var"], ["a", "d"]), ("else", ["var"], ["e"])]:
    block = conditional[side]
    assert block["k"] == "valueBlock" and block["type"] == unit, block
    assert not block.get("joinNullBranch", False), block
    assert block["result"] == {"k": "const", "type": unit, "value": None}, block
    assert [statement["k"] for statement in block["stmts"]] == kinds, block
    calls = [node for node in objects(block["stmts"]) if node.get("method") == "unitBlockEffect"]
    assert [call["args"][0]["value"] for call in calls] == tags, calls
print("Unit-valued blocks retain leading effects, declaration initializers and an explicit Kotlin Unit result")
