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
        "usage: assert-unchecked-generic-unsafe-accessor-cir.py "
        "<UncheckedGenericCastReturnTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as handle:
    root = json.load(handle)

owners = [item for item in root.get("types", []) if item.get("name") == "ProtectedIntArithmetic"]
if len(owners) != 1:
    raise SystemExit(f"found {len(owners)} ProtectedIntArithmetic declarations, expected 1")

methods = {
    method.get("name"): method
    for method in owners[0].get("methods", [])
    if method.get("name") in {"add", "negate"}
}
if set(methods) != {"add", "negate"}:
    raise SystemExit(f"missing protected unchecked-cast operator fixtures: {sorted(methods)}")

int_type = {"t": "fqn", "name": "System.Int32"}
object_type = {"t": "fqn", "name": "object"}
calls = []
for method in methods.values():
    matches = [
        node
        for node in objects(method.get("body", []))
        if node.get("k") == "callStatic"
        and node.get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
        and node.get("method", "").startswith("dotkt$unsafe$")
        and node.get("method", "").endswith("$invoke")
    ]
    if len(matches) != 1 or matches[0].get("ret") != int_type:
        raise SystemExit(
            f"{method['name']} must retain exactly one concrete Int use projection over its UnsafeAccessor: "
            f"{matches!r}"
        )
    calls.append(matches[0])

holder_names = {call["owner"]["name"] for call in calls}
if len(holder_names) != 1:
    raise SystemExit(f"operator calls disagree on their generated holder: {holder_names!r}")
holders = [item for item in root.get("types", []) if item.get("name") in holder_names]
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} matching UnsafeAccessor holders, expected 1")

entry_names = {call["method"] for call in calls}
entries = [method for method in holders[0].get("methods", []) if method.get("name") in entry_names]
if len(entries) != 1 or entries[0].get("ret") != object_type:
    raise SystemExit(
        "the generated wrapper must retain the deferred unchecked-cast physical object return: "
        f"{entries!r}"
    )

nullable_owners = [
    item for item in root.get("types", []) if item.get("name") == "ProtectedNullableInt"
]
if len(nullable_owners) != 1:
    raise SystemExit(
        f"found {len(nullable_owners)} ProtectedNullableInt declarations, expected 1"
    )
nullable_calls = [
    node
    for node in objects(nullable_owners[0].get("methods", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name", "").startswith("dotkt$unsafe$holder$")
    and "$prop_get_stored_$invoke" in node.get("method", "")
]
nullable_int = {"t": "nullable", "of": int_type}
nullable_casts = [
    node
    for node in objects(nullable_owners[0].get("methods", []))
    if node.get("k") == "cast"
    and node.get("type") == nullable_int
    and node.get("e") in nullable_calls
]
if len(nullable_calls) != 1 or nullable_calls[0].get("ret") != object_type or len(nullable_casts) != 1:
    raise SystemExit(
        "the real nullable-generic accessor must retain object and project to nullable Int at its use: "
        f"calls={nullable_calls!r}, casts={nullable_casts!r}"
    )

byref_object = {"t": "byRef", "of": object_type}
field_loads = [
    node
    for node in objects(root)
    if node.get("k") == "byrefLoad"
    and node.get("elem") == object_type
    and node.get("ptr", {}).get("k") == "callStatic"
    and node["ptr"].get("method", "").endswith("$stored$invoke")
    and node["ptr"].get("ret") == byref_object
]
field_casts = [
    node
    for node in objects(root)
    if node.get("k") == "cast"
    and node.get("type") == nullable_int
    and node.get("e") in field_loads
]
if len(field_loads) != 2 or len(field_casts) != 2:
    raise SystemExit(
        "the inline private nullable field must use byref<object> accessors before nullable Int projection: "
        f"loads={field_loads!r}, casts={field_casts!r}"
    )

print(
    "UnsafeAccessor distinguishes unchecked-generic object ABI from nullable-generic method/field erasure"
)
