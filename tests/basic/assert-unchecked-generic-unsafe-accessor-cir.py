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
        if node.get("k") == "callInstance" and node.get("method") == "read"
        and node.get("ownerType") == {"t": "fqn", "name": "ProtectedGenericCast", "args": [int_type]}
    ]
    casts = [node for node in objects(method.get("body", []))
             if node.get("k") == "cast" and node.get("type") == int_type and node.get("e") in matches]
    if (len(matches) != 1 or len(casts) != 1 or matches[0].get("sig") != [{"t": "fqn", "name": "System.Object"}]
            or matches[0].get("dynRet") != object_type or matches[0].get("calleeRet") != object_type):
        raise SystemExit(
            f"{method['name']} must project the direct call's physical object return before its operator: "
            f"{matches!r}"
        )
    operator = method["body"][0]["value"]
    operand = operator.get("lhs") if method["name"] == "add" else operator.get("e")
    if operand is not casts[0]:
        raise SystemExit(f"{method['name']} extracts the concrete value after rather than before its operator")
    calls.append(matches[0])

base = next(owner for owner in root["types"] if owner.get("name") == "ProtectedGenericCast")
read = next(method for method in base["methods"] if method.get("name") == "read")
if read.get("ret") != object_type:
    raise SystemExit(f"the selected declaration lost its deferred object return: {read!r}")

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
    if node.get("k") == "callInstance" and node.get("method") == "prop_get<stored>"
    and node.get("ownerType") == {"t": "fqn", "name": "ProtectedNullableProperty", "args": [int_type]}
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
