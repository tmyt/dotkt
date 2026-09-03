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
    raise SystemExit(
        "usage: assert-referenced-method-generic-unsafe-accessor-cir.py "
        "<ReferencedProtectedMethodGenericTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

closures = [
    item
    for item in root.get("types", [])
    if item.get("nestedIn") == "ReferencedProtectedMethodGenericText"
]
if len(closures) != 1:
    raise SystemExit(f"found {len(closures)} lifted closures, expected 1")

accessors = [
    method
    for method in closures[0].get("methods", [])
    if method.get("generated")
    and method.get("static")
    and method.get("extern")
    and method.get("name", "").startswith("dotkt$unsafe$")
]
if len(accessors) != 1:
    raise SystemExit(f"found {len(accessors)} generated UnsafeAccessor methods, expected 1")

accessor = accessors[0]
base = {"t": "fqn", "name": "roundtrip.protectedmethodgeneric.ReferencedProtectedMethodGenericBase"}
object_array = {"t": "array", "elem": {"t": "fqn", "name": "object"}}
expected_params = [base, object_array]
actual_params = [param.get("type") for param in accessor.get("params", [])]
if actual_params != expected_params:
    raise SystemExit(
        "method-generic UnsafeAccessor must declare target plus the original value parameter: "
        f"{accessor!r}"
    )
if accessor.get("typeParams") != ["__method0"] or accessor.get("ret") != object_array:
    raise SystemExit(f"method-generic UnsafeAccessor lost its generic frame or physical return: {accessor!r}")

calls = [
    node
    for node in objects(closures[0].get("methods", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name") == closures[0].get("name")
    and node.get("method") == accessor.get("name")
]
if len(calls) != 1:
    raise SystemExit(f"found {len(calls)} calls to the generated UnsafeAccessor, expected 1")

call = calls[0]
if (
    call.get("sig") != expected_params
    or len(call.get("args", [])) != 2
    or call.get("typeArgs") != [{"t": "fqn", "name": "System.String"}]
    or call.get("ret") != object_array
):
    raise SystemExit(f"UnsafeAccessor call and declaration are not arity/type coherent: {call!r}")

print("referenced method-generic UnsafeAccessor retains target and value-parameter slots")
