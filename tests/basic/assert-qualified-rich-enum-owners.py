#!/usr/bin/env python3
"""Enum reads stay semantic in BIR and retain selected owners after physical lowering."""

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
        semantic = path == sys.argv[1]
        operation = "enumName" if field == "__name" else "enumOrdinal"
        reads = [node for node in objects(methods[0]["body"])
                 if node.get("k") == operation] if semantic else [
                     node for node in objects(methods[0]["body"])
                     if node.get("k") == "field" and node.get("name") == field]
        assert len(reads) == 1, (path, method_name, reads)
        actual = reads[0]["type" if semantic else "ownerType"]
        expected_type = {"t": "fqn", "name": owner}
        if semantic and owner == "privateenumprobe.NestedEnumOwner.State":
            expected_type["classifier"] = {
                "packageName": "privateenumprobe", "ownerPath": ["NestedEnumOwner"], "name": "State",
            }
        assert actual == expected_type, (path, method_name, actual)
        if semantic:
            assert not any(node.get("k") == "field" for node in objects(methods[0]["body"])), (path, method_name)
    comparison = [node for node in objects(root) if node.get("name") == "publicCompare" and "body" in node]
    assert len(comparison) == 1, (path, comparison)
    body = list(objects(comparison[0]["body"]))
    if path == sys.argv[1]:
        ordinals = [node for node in body if node.get("k") == "enumOrdinal"]
        assert len(ordinals) == 2 and all(node["type"] == {"t": "fqn", "name": "publicenumprobe.State"}
                                          for node in ordinals), (path, ordinals)
        assert not any(node.get("k") == "field" for node in body), path
    else:
        ordinals = [node for node in body if node.get("k") == "field" and node.get("name") == "__ordinal"]
        assert len(ordinals) == 2 and all(node["ownerType"] == {"t": "fqn", "name": "publicenumprobe.State"}
                                          for node in ordinals), (path, ordinals)
    fields = [node for node in objects(root)
              if node.get("k") == "field" and node.get("name") in ("__name", "__ordinal")]
    assert fields and all(node["ownerType"].get("name") != "State" for node in fields), path

print("Semantic BIR enum reads/comparison lower to exact qualified private, public and nested storage owners")
