#!/usr/bin/env python3
"""Explicit non-null reference casts retain their Kotlin null rejection, not internal casts."""
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


def body(name):
    methods = [node for node in objects(root) if node.get("name") == name and "body" in node]
    assert len(methods) == 1, (name, methods)
    return list(objects(methods[0]["body"]))


def null_throws(nodes):
    return [node for node in nodes if node.get("k") == "new"
            and node.get("type") == {"t": "fqn", "name": "kotlin.NullPointerException"}]


for name in ("checkedString", "checkedAny", "checkedClass", "checkedInterface",
             "checkedNativeInterface", "checkedArray", "checkedProjectedArray", "checkedProjectedNative",
             "checkedNamedParameter"):
    nodes = body(name)
    assert len(null_throws(nodes)) == 1, name
    casts = [node for node in nodes if node.get("k") == "cast"]
    assert len(casts) == 1, (name, casts)
    operand = casts[0]["e"]
    assert operand["k"] == "valueBlock", (name, operand)
    assert len(null_throws(list(objects(operand)))) == 1, name
    bindings = [node for node in operand["stmts"] if node.get("k") == "var"]
    assert len(bindings) == 1, (name, bindings)
    assert "$" in bindings[0]["name"], (name, bindings)
    assert bindings[0]["type"] == {"t": "nullable", "of": {"t": "fqn", "name": "kotlin.Any"}}, (name, bindings)
    assert bindings[0]["init"]["k"] == "local", (name, bindings)
    assert any(node.get("k") == "objEq" for node in nodes), name

for name in ("unchecked", "nullableString", "safeString", "nullableProjectedArray", "safeProjectedArray",
             "nullableProjectedNative", "safeProjectedNative"):
    assert not null_throws(body(name)), name

print("Explicit non-null reference casts bind/check their source before casting; nullable/safe/unchecked casts do not")
