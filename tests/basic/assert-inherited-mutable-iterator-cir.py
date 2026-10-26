#!/usr/bin/env python3
"""Pin source-selected inherited slots across a local/reference carrier boundary."""
import json
import sys


def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)


with open(sys.argv[1], encoding="utf-8") as stream:
    bir = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    cir = json.load(stream)
source = next(method for method in bir["methods"] if method["name"] == "verifyInheritedIteratorOperations")
lowered = next(method for method in cir["methods"] if method["name"] == source["name"])
source_calls = [node for node in nodes(source["body"])
                if node.get("k") == "callInstance" and node.get("method") == "contains"]
assert len(source_calls) == 2
for call in source_calls:
    assert call.get("declarationId")
    assert any(edge.get("owner", {}).get("name") == "kotlin.collections.AbstractMutableCollection"
               and edge.get("declarationId") == call["declarationId"] for edge in call["overrides"])
calls = [node for node in nodes(lowered["body"])
         if node.get("k") == "callInstance" and node.get("method") == "$star$contains$3"]
assert len(calls) == 2
for call in calls:
    selected = call["memberRef"]
    assert selected["assembly"] == "DotKt.Stdlib"
    assert selected["declaringType"] == {"t": "fqn", "name": "kotlin.collections.AbstractMutableCollection$star"}
    assert selected["name"] == call["method"] and selected["genericArity"] == 0
    assert selected["parameterTypes"] == [{"t": "fqn", "name": "System.Object"}]
    assert selected["returnType"] == {"t": "fqn", "name": "System.Boolean"}
    assert call["ownerType"] == selected["declaringType"]
    assert not any(node.get("t") == "tv" and node.get("scope") == "type" for node in nodes(call["sig"]))
    assert not any(node.get("t") == "tv" and node.get("scope") == "type" for node in nodes(call["args"]))
print("Inherited source declaration binds its exact referenced carrier slot without leaking owner variables")
