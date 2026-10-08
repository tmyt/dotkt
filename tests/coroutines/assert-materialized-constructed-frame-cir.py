#!/usr/bin/env python3
"""Assert that #619 instantiates an intrinsic closure in its enclosing owner's frame exactly once."""

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
        "usage: assert-materialized-constructed-frame-cir.py "
        "<MaterializedLambdaCaptureTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

owner_t = {"t": "tv", "scope": "type", "i": 0}
object_type = {"t": "fqn", "name": "System.Object"}
source_list = {"t": "fqn", "name": "kotlin.collections.List", "args": [owner_t]}
# List<T> is an opaque value representation. Its CLR argument is object, while
# the source result and signature selector must still retain the exact owner T.
constructed_box = {
    "t": "fqn",
    "name": "MaterializedConstructedBox",
    "args": [object_type],
}
mixed_box = {
    "t": "fqn",
    "name": "MaterializedMixedBox",
    "args": [
        object_type,
        owner_t,
    ],
}

for owner_name, method_name, state_machine_name, box_name, expected in (
    ("MaterializedConstructedOwner", "awaitList", "MaterializedConstructedOwner_awaitList$sm", "MaterializedConstructedBox", constructed_box),
    ("MaterializedMixedOwner", "awaitTaggedList", "MaterializedMixedOwner_awaitTaggedList$sm", "MaterializedMixedBox", mixed_box),
):
    owners = [item for item in root.get("types", []) if item.get("name") == owner_name]
    if len(owners) != 1 or len(owners[0].get("typeParams", [])) != 1:
        raise SystemExit(f"CIR: {owner_name} must declare exactly its source T")
    methods = [item for item in owners[0].get("methods", []) if item.get("name") == method_name]
    if len(methods) != 1:
        raise SystemExit(f"CIR: expected exactly one {owner_name}.{method_name}")
    method = methods[0]
    expected_result = {
        "t": "fqn", "name": "System.Threading.Tasks.Task", "args": [{
            "t": "mod", "req": False,
            "m": {**source_list, "name": "kotlin.collections.List`1"},
            "of": object_type,
        }],
    }
    if method.get("ret") != expected_result:
        raise SystemExit(f"CIR: {owner_name}.{method_name} lost its exact List<T> result selector")
    carriers = [attribute for attribute in method.get("attrs", [])
                if attribute.get("attr", {}).get("name")
                == "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute"]
    if len(carriers) != 1:
        raise SystemExit(f"CIR: {owner_name}.{method_name} must preserve one source result carrier")
    arguments = carriers[0].get("args", [])
    if len(arguments) != 2 or arguments[0].get("value") != "bir-json/1":
        raise SystemExit(f"CIR: {owner_name}.{method_name} has a malformed source result carrier")
    declared_result = json.loads(base64.b64decode(arguments[1]["bytes"], validate=True))
    if declared_result != source_list:
        raise SystemExit(f"CIR: {owner_name}.{method_name} lost its source List<T> owner frame")
    state_machines = [
        item
        for item in root.get("types", [])
        if isinstance(item, dict) and item.get("name") == state_machine_name
    ]
    if len(state_machines) != 1:
        raise SystemExit(
            f"CIR: found {len(state_machines)} {state_machine_name} state machines, expected 1"
        )

    state_machine = state_machines[0]
    frame = state_machine.get("capturedTypeParams", []) + state_machine.get("typeParams", [])
    if len(frame) != 1:
        raise SystemExit(
            f"CIR: {state_machine_name} does not declare exactly its enclosing owner's T"
        )

    constructions = [
        item
        for item in objects(state_machine.get("methods", []))
        if item.get("k") == "new"
        and isinstance(item.get("type"), dict)
        and item["type"].get("name") == box_name
    ]
    if len(constructions) != 1:
        raise SystemExit(
            f"CIR: found {len(constructions)} {box_name} constructions in {state_machine_name}, "
            "expected 1"
        )
    if constructions[0].get("type") != expected:
        raise SystemExit(
            f"CIR: {box_name} has the wrong enclosing-owner specialization: "
            f"{constructions[0].get('type')!r}"
        )

print("materialized constructed intrinsic frame OK (physical slots + exact source result)")
