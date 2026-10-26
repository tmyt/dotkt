#!/usr/bin/env python3
"""Assert that #557's inline-spliced suspend carrier captures only its caller-frame T."""

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


def kotlin_type(declaration):
    attributes = [
        item for item in declaration.get("attrs", [])
        if item.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices.KotlinTypeAttribute"
    ]
    if len(attributes) != 1:
        raise SystemExit(f"CIR: expected one KotlinType carrier, got {len(attributes)}")
    arguments = attributes[0].get("args", [])
    if len(arguments) != 2 or arguments[0].get("value") != "bir-json/1":
        raise SystemExit("CIR: KotlinType carrier has the wrong codec envelope")
    return json.loads(base64.b64decode(arguments[1]["bytes"], validate=True))


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
helper_declarations = [item for item in root.get("types", []) if item.get("name") == helper["type"]["name"]]
if len(helper_declarations) != 1 or len(helper_declarations[0].get("ctors", [])) != 1:
    raise SystemExit("CIR: expected one selected helper constructor declaration")
parameters = helper_declarations[0]["ctors"][0].get("params", [])
if len(parameters) != 1 or helper.get("argTypes") != [parameters[0].get("type")]:
    raise SystemExit("CIR: helper application and its selected constructor disagree on the physical collector slot")
semantic_collector = {"t": "fqn", "name": "CrossinlineGenericCollector", "args": [carrier_t]}
if kotlin_type(parameters[0]) != semantic_collector:
    raise SystemExit("CIR: helper constructor metadata lost caller method#1 to carrier type#0 renumbering")
collector_slot = parameters[0]["type"]
if collector_slot.get("t") != "fqn" or collector_slot.get("args"):
    raise SystemExit("CIR: helper constructor collector slot must be a non-generic carrier")
collector_carriers = [item for item in root.get("types", []) if item.get("name") == collector_slot.get("name")]
if len(collector_carriers) != 1 or collector_carriers[0].get("kind") != "interface" or collector_carriers[0].get("typeParams"):
    raise SystemExit("CIR: collector slot has no unique non-generic interface declaration")
if kotlin_type(collector_carriers[0]) != {"t": "fqn", "name": "CrossinlineGenericCollector", "args": [{"t": "star"}]}:
    raise SystemExit("CIR: collector carrier lost its semantic classifier association")
collector_declarations = [item for item in root.get("types", []) if item.get("name") == "CrossinlineGenericCollector"]
if len(collector_declarations) != 1 or collector_slot not in collector_declarations[0].get("interfaces", []):
    raise SystemExit("CIR: source collector declaration does not implement its selected carrier slot")

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
