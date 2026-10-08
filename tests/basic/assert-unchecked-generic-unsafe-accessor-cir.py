#!/usr/bin/env python3
import base64
import copy
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
        and node.get("owner", {}).get("name") == "ProtectedGenericCast"
        and node.get("method", "").startswith("dotkt$unsafe$")
        and node.get("method", "").endswith("$invoke")
    ]
    projections = [node for node in objects(method.get("body", []))
                   if node.get("k") == "cast" and node.get("type") == int_type
                   and node.get("e") in matches]
    if (len(matches) != 1 or matches[0].get("ret") != object_type
            or matches[0]["owner"].get("args") != [int_type] or len(projections) != 1):
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
if (len(entries) != 1 or entries[0].get("ret") != object_type
        or len(holders[0].get("typeParams", [])) != 1):
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
    and node.get("owner", {}).get("name") == "ProtectedNullableProperty"
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

field_loads = [
    node
    for node in objects(root)
    if node.get("k") == "callInstance"
    and node.get("ownerType") == {"t": "fqn", "name": "InlinePrivateNullableField$star"}
    and node.get("method") == "$star$dotkt:field:get:stored$1"
    and node.get("virtual") is True and node.get("sig") == []
    and node.get("ret") == object_type
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
        "the inline private nullable field must use its object carrier slot before nullable Int projection: "
        f"loads={field_loads!r}, casts={field_casts!r}"
    )
field_carriers = [owner for owner in root.get("types", [])
                  if owner.get("name") == "InlinePrivateNullableField$star"]
field_slots = [method for owner in field_carriers for method in owner.get("methods", [])
               if method.get("name") == "$star$dotkt:field:get:stored$1"]
if (len(field_carriers) != 1 or field_carriers[0].get("kind") != "interface"
        or len(field_slots) != 1 or field_slots[0].get("abstract") is not True
        or field_slots[0].get("ret") != object_type or field_slots[0].get("params") != []):
    raise SystemExit("private inline reads lack their exact declared object carrier slot")

def source_result(method, expected):
    carriers = [attribute for attribute in method.get("retAttrs", [])
                if attribute.get("attr", {}).get("name")
                == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"]
    if len(carriers) != 1:
        raise ValueError("expected one exact Kotlin source result carrier")
    arguments = carriers[0].get("args", [])
    if (len(arguments) != 2 or arguments[0].get("value") != "bir-json/1"
            or json.loads(base64.b64decode(arguments[1]["bytes"], validate=True)) != expected):
        raise ValueError("Kotlin source result lost its declaration frame")


source_t = {"t": "tv", "scope": "type", "i": 0}
source_result(entries[0], source_t)
externs = [method for method in holders[0].get("methods", [])
           if method.get("extern") and method.get("name") == entries[0]["name"].removesuffix("$invoke")]
expected_owner = {"t": "fqn", "name": "ProtectedGenericCast", "args": [source_t]}
if (len(externs) != 1 or externs[0].get("ret") != object_type
        or [parameter.get("type") for parameter in externs[0].get("params", [])]
        != [expected_owner, {"t": "fqn", "name": "System.Object"}]):
    raise SystemExit("owned UnsafeAccessor lost its exact target or object signature")
source_result(externs[0], source_t)
target_names = [argument.get("value", {}).get("value")
                for attribute in externs[0].get("attrs", [])
                if attribute.get("attr", {}).get("name") == "System.Runtime.CompilerServices.UnsafeAccessorAttribute"
                for argument in attribute.get("namedArgs", []) if argument.get("name") == "Name"]
if target_names != ["read"]:
    raise SystemExit(f"owned UnsafeAccessor targets the wrong declaration: {target_names!r}")


def enclosing_contract(method):
    if method.get("ret") != object_type or method.get("typeParams") != ["T"]:
        raise ValueError("enclosing generic function lost its physical return or method frame")
    source_result(method, {"t": "tv", "scope": "method", "i": 0})


for name in ("throughNestedClosure", "throughNestedSam"):
    declarations = [method for method in root.get("methods", []) if method.get("name") == name]
    if len(declarations) != 1:
        raise SystemExit(f"expected one {name} declaration")
    enclosing_contract(declarations[0])
    for slot in ("retAttrs", "typeParams"):
        malformed = copy.deepcopy(declarations[0])
        malformed[slot] = []
        try:
            enclosing_contract(malformed)
        except ValueError:
            pass
        else:
            raise SystemExit(f"malformed current {slot} escaped the enclosing contract guard")
    malformed = copy.deepcopy(declarations[0])
    carrier = next(attribute for attribute in malformed["retAttrs"]
                   if attribute.get("attr", {}).get("name")
                   == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute")
    carrier["args"][1]["bytes"] = base64.b64encode(
        json.dumps({"t": "tv", "scope": "method", "i": 1}).encode()).decode()
    try:
        enclosing_contract(malformed)
    except ValueError:
        pass
    else:
        raise SystemExit("wrong current source generic index escaped the enclosing contract guard")

print("nested closure/SAM preserve enclosing Kotlin result frames (six malformed-current mutations rejected)")
