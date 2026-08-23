#!/usr/bin/env python3
"""Assert that #557's inline-spliced suspend carrier captures only its caller-frame T."""

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
    raise SystemExit("usage: assert-inline-suspend-frame-cir.py <CrossinlineSuspendObjectTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

state_machines = [
    item
    for item in root.get("types", [])
    if isinstance(item, dict)
    and isinstance(item.get("name"), str)
    and item["name"].startswith("CrossinlineSuspendObjectTestsKt_crossinlineGenericWrap_lambda")
    and item["name"].endswith("$sm")
]
if len(state_machines) != 1:
    raise SystemExit(f"CIR: found {len(state_machines)} crossinlineGenericWrap carriers, expected exactly 1")

state_machine = state_machines[0]
type_params = state_machine.get("typeParams", [])
if len(type_params) != 1:
    raise SystemExit(
        f"CIR: {state_machine['name']} has {len(type_params)} generic parameters, expected caller T only"
    )

constructions = [
    item
    for item in objects(root.get("methods", []))
    if item.get("k") == "new"
    and isinstance(item.get("type"), dict)
    and item["type"].get("name") == state_machine["name"]
]
if len(constructions) != 1:
    raise SystemExit(f"CIR: found {len(constructions)} constructions of {state_machine['name']}, expected 1")

type_args = constructions[0]["type"].get("args", [])
expected = [{"t": "tv", "scope": "method", "i": 0}]
if type_args != expected:
    raise SystemExit(f"CIR: {state_machine['name']} construction has wrong enclosing frame: {type_args!r}")

print("inline suspend carrier generic frame OK")
