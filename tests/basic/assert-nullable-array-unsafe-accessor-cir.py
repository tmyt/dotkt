#!/usr/bin/env python3
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


if len(sys.argv) != 2:
    raise SystemExit("usage: assert-nullable-array-unsafe-accessor-cir.py <NullableTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as handle:
    root = json.load(handle)

derived = [item for item in root.get("types", []) if item.get("name") == "NgProtectedArrayText"]
if len(derived) != 1:
    raise SystemExit(f"found {len(derived)} NgProtectedArrayText declarations, expected 1")
snapshots = [method for method in derived[0].get("methods", []) if method.get("name") == "snapshot"]
if len(snapshots) != 1:
    raise SystemExit(f"found {len(snapshots)} snapshot methods, expected 1")

string_array = {"t": "array", "elem": {"t": "fqn", "name": "System.String"}}
object_array = {"t": "array", "elem": {"t": "fqn", "name": "object"}}
projections = [
    node
    for node in objects(snapshots[0].get("body", []))
    if node.get("k") == "cast"
    and node.get("type") == string_array
    and node.get("e", {}).get("k") == "callStatic"
    and node["e"].get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(projections) != 1:
    raise SystemExit(
        "inherited nullable-generic array read must have exactly one explicit UnsafeAccessor result projection: "
        f"{projections!r}"
    )
inner = projections[0]["e"]
if inner.get("ret") != object_array:
    raise SystemExit(f"UnsafeAccessor call does not state the physical object[] result: {inner!r}")

holder_name = inner["owner"]["name"]
holders = [item for item in root.get("types", []) if item.get("name") == holder_name]
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} matching UnsafeAccessor holders, expected 1")
entries = [method for method in holders[0].get("methods", []) if method.get("name") == inner.get("method")]
if len(entries) != 1 or entries[0].get("ret") != object_array:
    raise SystemExit(f"UnsafeAccessor wrapper disagrees with its call-site physical result: {entries!r}")

captured_projections = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "setField"
    and node.get("name") == "v"
    and node.get("ownerType", {}).get("name", "").startswith("dotkt$NullableTestsKt$Ref$")
    and node.get("value", {}).get("k") == "cast"
    and node["value"].get("type") == string_array
    and node["value"].get("e", {}).get("k") == "callStatic"
    and node["value"]["e"].get("ret") == object_array
    and node["value"]["e"].get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(captured_projections) != 1:
    raise SystemExit(
        "captured inherited nullable-generic array read must project object[] before the synthesized ref-cell store: "
        f"{captured_projections!r}"
    )

method_closures = [item for item in root.get("types", []) if item.get("nestedIn") == "NgProtectedMethodText"]
if len(method_closures) != 1:
    raise SystemExit(f"found {len(method_closures)} NgProtectedMethodText closure types, expected 1")
method_accessors = [
    method
    for method in method_closures[0].get("methods", [])
    if method.get("generated")
    and method.get("static")
    and method.get("extern")
    and method.get("name", "").startswith("dotkt$unsafe$")
    and method.get("ret") == object_array
]
if len(method_accessors) != 1:
    raise SystemExit(f"non-generic owner must materialize one object[] UnsafeAccessor on the closure: {method_accessors!r}")
method_projections = [
    node
    for node in objects(method_closures[0].get("methods", []))
    if node.get("k") == "setField"
    and node.get("name") == "v"
    and node.get("value", {}).get("k") == "cast"
    and node["value"].get("type") == string_array
    and node["value"].get("e", {}).get("k") == "callStatic"
    and node["value"]["e"].get("owner", {}).get("name") == method_closures[0]["name"]
    and node["value"]["e"].get("method") == method_accessors[0]["name"]
    and node["value"]["e"].get("ret") == object_array
]
if len(method_projections) != 1:
    raise SystemExit(
        "method-generic nullable array on a non-generic protected owner must project the caller-hosted accessor result: "
        f"{method_projections!r}"
    )

print("generic-owner and method-generic inherited nullable-array accesses state object[] plus concrete projections")
