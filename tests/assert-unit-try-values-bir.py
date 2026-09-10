#!/usr/bin/env python3
"""Try joins receive their Kotlin values before physical lowering, without a temporary-name protocol."""
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
methods = {method["name"]: method for method in bir["methods"]}


def join(name):
    blocks = [node for node in objects(methods[name]["body"])
              if node.get("k") == "valueBlock" and node.get("result", {}).get("k") == "local"
              and any(stmt.get("k") == "try" for stmt in node.get("stmts", []))]
    assert len(blocks) == 1, blocks
    block = blocks[0]
    result_name = block["result"]["name"]
    declaration = next(stmt for stmt in block["stmts"]
                       if stmt.get("k") == "var" and stmt.get("name") == result_name)
    body = next(stmt for stmt in block["stmts"] if stmt.get("k") == "try")
    return result_name, declaration, body


name, declaration, body = join("unitTryBranchValue")
unit = {"t": "fqn", "name": "kotlin.Unit"}
assert declaration["type"] == unit, declaration
for branch in [body["body"], *(catch["body"] for catch in body["catches"])]:
    last = branch[-1]
    assert last == {"k": "setLocal", "name": name,
                    "value": {"k": "const", "type": unit, "value": None}}, last
assert not any(node.get("k") == "setLocal" and node.get("name") == name
               for node in objects(body.get("finally", []))), body

name, _, body = join("unitTryNullableBranch")
last = body["catches"][0]["body"][-1]
assert last["k"] == "setLocal" and last["name"] == name, last
assert last["value"]["k"] == "const" and last["value"]["value"] is None, last
assert last["value"]["type"] != unit, last
print("Unit and null try branches explicitly assign their Kotlin result before physical lowering")
