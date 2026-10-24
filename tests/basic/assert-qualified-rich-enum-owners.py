#!/usr/bin/env python3
"""Local rich-enum metadata reads retain their selected declaration identity."""

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


expected = {
    "privateName": ("privateenumprobe.State", "__name"),
    "privateOrdinal": ("privateenumprobe.State", "__ordinal"),
    "publicName": ("publicenumprobe.State", "__name"),
    "publicOrdinal": ("publicenumprobe.State", "__ordinal"),
    "nestedName": ("privateenumprobe.NestedEnumOwner.State", "__name"),
    "nestedOrdinal": ("privateenumprobe.NestedEnumOwner.State", "__ordinal"),
}

if len(sys.argv) != 3:
    raise SystemExit("usage: assert-qualified-rich-enum-owners.py <fixture.bir.json> <fixture.cir.json>")

for path in sys.argv[1:]:
    with open(path, encoding="utf-8") as stream:
        root = json.load(stream)
    for method_name, (owner, field) in expected.items():
        methods = [node for node in objects(root)
                   if node.get("name") == method_name and "body" in node]
        assert len(methods) == 1, (path, method_name, methods)
        reads = [node for node in objects(methods[0]["body"])
                 if node.get("k") == "field" and node.get("name") == field]
        assert len(reads) == 1, (path, method_name, reads)
        actual = reads[0]["ownerType"]
        assert actual == {"t": "fqn", "name": owner}, (path, method_name, actual)
    fields = [node for node in objects(root)
              if node.get("k") == "field" and node.get("name") in ("__name", "__ordinal")]
    assert fields and all(node["ownerType"].get("name") != "State" for node in fields), path

print("Rich-enum name/ordinal reads retain qualified private, public and nested declaration owners")
