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

state_machines = [
    item
    for item in root.get("types", [])
    if isinstance(item, dict) and item.get("name") == "MaterializedConstructedOwner_awaitList$sm"
]
if len(state_machines) != 1:
    raise SystemExit(f"CIR: found {len(state_machines)} constructed-owner state machines, expected 1")

state_machine = state_machines[0]
frame = state_machine.get("capturedTypeParams", []) + state_machine.get("typeParams", [])
if len(frame) != 1:
    raise SystemExit(
        f"CIR: {state_machine['name']} does not declare exactly its enclosing owner's T"
    )

box_locals = [
    item
    for item in objects(state_machine.get("methods", []))
    if item.get("k") == "var"
    and isinstance(item.get("type"), dict)
    and item["type"].get("name") == "MaterializedConstructedBox"
]
if len(box_locals) != 1:
    raise SystemExit(f"CIR: found {len(box_locals)} constructed-box locals, expected 1")

owner_t = {"t": "tv", "scope": "type", "i": 0}
expected = {
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
if box_locals[0].get("type") != expected:
    raise SystemExit(
        "CIR: materialized box must be specialized to List<owner !0> exactly once: "
        f"{box_locals[0].get('type')!r}"
    )

type_variables = [
    item
    for item in objects(state_machine)
    if item.get("t") == "tv" and item.get("scope") == "type"
]
bad_slots = sorted({item.get("i") for item in type_variables if item.get("i") != 0})
if bad_slots:
    raise SystemExit(f"CIR: state machine references undeclared type slots: {bad_slots!r}")

print("materialized constructed intrinsic frame OK")
