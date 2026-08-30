#!/usr/bin/env python3
"""Assert that #619 instantiates an intrinsic closure in its enclosing owner's frame exactly once."""

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
constructed_box = {
    "t": "fqn",
    "name": "MaterializedConstructedBox",
    "args": [
        {
            "t": "fqn",
            "name": "System.Collections.Generic.IList",
            "args": [owner_t],
        }
    ],
}
mixed_box = {
    "t": "fqn",
    "name": "MaterializedMixedBox",
    "args": [
        {
            "t": "fqn",
            "name": "System.Collections.Generic.IList",
            "args": [owner_t],
        },
        owner_t,
    ],
}

for state_machine_name, box_name, expected in (
    ("MaterializedConstructedOwner_awaitList$sm", "MaterializedConstructedBox", constructed_box),
    ("MaterializedMixedOwner_awaitTaggedList$sm", "MaterializedMixedBox", mixed_box),
):
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

print("materialized constructed intrinsic frame OK")
