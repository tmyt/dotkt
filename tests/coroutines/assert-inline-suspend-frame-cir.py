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
expected = [{"t": "tv", "scope": "method", "i": 1}]
if type_args != expected:
    raise SystemExit(f"CIR: {state_machine['name']} construction has wrong enclosing frame: {type_args!r}")

helper_constructions = [
    item
    for item in objects(state_machine)
    if item.get("k") == "new"
    and isinstance(item.get("type"), dict)
    and item["type"].get("name") == "CrossinlineGenericSafeCollector"
]
if len(helper_constructions) != 1:
    raise SystemExit(f"CIR: found {len(helper_constructions)} generic helper constructions in the carrier, expected 1")

carrier_t = {"t": "tv", "scope": "type", "i": 0}
helper = helper_constructions[0]
if helper["type"].get("args") != [carrier_t]:
    raise SystemExit(f"CIR: helper construction type did not renumber caller method#1 to carrier type#0: {helper['type']!r}")
expected_arg_types = [{"t": "fqn", "name": "CrossinlineGenericCollector", "args": [carrier_t]}]
if helper.get("argTypes") != expected_arg_types:
    raise SystemExit(f"CIR: helper constructor application did not renumber with the carrier: {helper.get('argTypes')!r}")

local_frame_state_machines = [
    item
    for item in root.get("types", [])
    if isinstance(item, dict)
    and isinstance(item.get("name"), str)
    and item["name"].startswith("CrossinlineSuspendObjectTestsKt_crossinlineLocalGenericFrame_lambda")
    and item["name"].endswith("$sm")
]
if len(local_frame_state_machines) != 1:
    raise SystemExit(
        f"CIR: found {len(local_frame_state_machines)} local-generic-function carriers, expected exactly 1"
    )

local_frame_state_machine = local_frame_state_machines[0]
if local_frame_state_machine.get("typeParams"):
    raise SystemExit(
        f"CIR: local function declaration frame leaked into {local_frame_state_machine['name']}: "
        f"{local_frame_state_machine['typeParams']!r}"
    )

print("inline suspend carrier generic frame OK")
