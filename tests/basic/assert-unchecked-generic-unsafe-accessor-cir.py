#!/usr/bin/env python3
import base64
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

field_owner = next(owner for owner in root["types"] if owner.get("name") == "InlinePrivateNullableField")
field = next(field for field in field_owner["fields"] if field.get("name") == "stored")
nullable_slot = {"t": "tv", "scope": "type", "i": 1}
if field.get("type") != nullable_slot:
    raise SystemExit(f"native nullable field lost its declared companion slot: {field!r}")
field_logical = [json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
                 for attribute in field.get("attrs", [])
                 if attribute.get("attr", {}).get("name")
                 == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"]
if field_logical != [{"t": "nullable", "of": {"t": "tv", "scope": "type", "i": 0}}]:
    raise SystemExit(f"native field lost its logical T? metadata: {field_logical!r}")
field_carrier = {"t": "fqn", "name": "InlinePrivateNullableField$star"}
field_bridges = [method for method in field_owner["methods"]
                 if any(slot.get("owner") == field_carrier and slot.get("params") == []
                        and slot.get("ret") == {"t": "fqn", "name": "System.Object"}
                        for slot in method.get("clrInterfaceImpls", []))]
if len(field_bridges) != 1:
    raise SystemExit(f"native field lost its ordinary value getter bridge: {field_bridges!r}")
bridge = field_bridges[0]
boxed_reads = [node for node in objects(bridge["body"])
               if node.get("k") == "cast" and node.get("type") == {"t": "fqn", "name": "System.Object"}
               and node.get("e", {}).get("k") == "callInstance" and node["e"].get("ret") == nullable_slot]
if len(boxed_reads) != 1:
    raise SystemExit(f"native field getter did not box its actual nullable slot: {boxed_reads!r}")
field_loads = [
    node
    for node in objects(root)
    if node.get("k") == "callInstance" and node.get("ownerType") == field_carrier
    and node.get("method") == bridge["name"] and node.get("sig") == []
    and node.get("ret") == {"t": "fqn", "name": "System.Object"}
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
        "the inlined private native field must use its carrier getter before nullable Int projection: "
        f"loads={field_loads!r}, casts={field_casts!r}"
    )

print(
    "unchecked generic method ABI and native nullable field layout remain distinct from ordinary value reads"
)
