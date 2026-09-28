#!/usr/bin/env python3
"""Check constructed receiver facts on generic suspend super forwarders."""
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

types = {item["name"]: item for item in root["types"]}
expected = {"GenericSuperChild", "GenericSuperPairChild", "GenericSuperPausedChild"}
seen = set()
for call in objects(root):
    if call.get("k") != "callInstance" or not call.get("method", "").startswith("dotkt$super$"):
        continue
    owner = call["ownerType"]
    if owner.get("name") not in expected:
        continue
    seen.add(owner["name"])
    assert owner.get("args"), f"bare generic forwarder owner: {call}"
    assert call.get("virtual") is False, f"super forwarder must be nonvirtual: {call}"
    receiver = call["recv"]
    assert receiver["k"] == "field" and receiver["name"] == "$this", receiver
    storage = next(field for field in types[receiver["ownerType"]["name"]]["fields"]
                   if field["name"] == "$this")
    assert receiver["ret"] == storage["type"] == owner, (receiver, storage, owner)
assert seen == expected, (seen, expected)
print("generic suspend super receivers retain exact constructed field types")
