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
string_type = string_array["elem"]
owner_array = {"t": "array", "elem": {"t": "tv", "scope": "type", "i": 1}}
method_array = {"t": "array", "elem": {"t": "tv", "scope": "method", "i": 1}}
projections = [
    node
    for node in objects(snapshots[0].get("body", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(projections) != 1:
    raise SystemExit(
        "inherited nullable-generic array read must have exactly one framed UnsafeAccessor call: "
        f"{projections!r}"
    )
inner = projections[0]
if inner.get("ret") != string_array or inner["owner"].get("args") != [string_type, string_type]:
    raise SystemExit(f"UnsafeAccessor call does not close the ordinary/nullable owner frame to string[]: {inner!r}")

holder_name = inner["owner"]["name"]
holders = [item for item in root.get("types", []) if item.get("name") == holder_name]
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} matching UnsafeAccessor holders, expected 1")
entries = [method for method in holders[0].get("methods", []) if method.get("name") == inner.get("method")]
if len(entries) != 1 or entries[0].get("ret") != owner_array or len(holders[0].get("typeParams", [])) != 2:
    raise SystemExit(f"UnsafeAccessor wrapper disagrees with its call-site physical result: {entries!r}")
externs = [method for method in holders[0].get("methods", []) if method.get("extern")]
if len(externs) != 1 or externs[0].get("ret") != owner_array:
    raise SystemExit(f"UnsafeAccessor extern must retain its owner's nullable companion result: {externs!r}")
expected_target = {"t": "fqn", "name": "NgProtectedArrayBase", "args": [
    {"t": "tv", "scope": "type", "i": 0}, {"t": "tv", "scope": "type", "i": 1}
]}
if externs[0].get("params", [{}])[0].get("type") != expected_target:
    raise SystemExit(f"UnsafeAccessor target lost its complete owner frame: {externs!r}")

captured_projections = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "setField"
    and node.get("name") == "v"
    and node.get("ownerType", {}).get("name", "").startswith("dotkt$NullableTestsKt$Ref$")
    and node.get("value", {}).get("k") == "callStatic"
    and node["value"].get("ret") == string_array
    and node["value"].get("owner", {}).get("args") == [string_type, string_type]
    and node["value"].get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(captured_projections) != 1:
    raise SystemExit(
        "captured inherited nullable-generic array read must close its companion before the ref-cell store: "
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
    and method.get("ret") == method_array
    and len(method.get("typeParams", [])) == 2
]
if len(method_accessors) != 1:
    raise SystemExit(f"non-generic owner must materialize one method-framed UnsafeAccessor on the closure: {method_accessors!r}")
if method_accessors[0].get("params", [{}, {}])[1].get("type") != method_array:
    raise SystemExit(f"method-framed UnsafeAccessor input disagrees with its nullable result: {method_accessors!r}")
method_projections = [
    node
    for node in objects(method_closures[0].get("methods", []))
    if node.get("k") == "setField"
    and node.get("name") == "v"
    and node.get("value", {}).get("k") == "callStatic"
    and node["value"].get("owner", {}).get("name") == method_closures[0]["name"]
    and node["value"].get("method") == method_accessors[0]["name"]
    and node["value"].get("ret") == string_array
    and node["value"].get("typeArgs") == [string_type, string_type]
]
if len(method_projections) != 1:
    raise SystemExit(
        "method-generic nullable array must close the caller-hosted accessor's complete method frame: "
        f"{method_projections!r}"
    )

print("generic-owner and method-generic inherited nullable-array accessors close their nullable companion frames")
