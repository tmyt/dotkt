#!/usr/bin/env python3
"""Assert that #436's super-qualified suspend calls keep their physical CIR dispatch."""

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
    raise SystemExit("usage: assert-suspend-super-cir.py <SuspendDispatchTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

types = {item.get("name"): item for item in root.get("types", []) if isinstance(item, dict)}
targets = {
    "SuspendDispatchSuperPlain_callBase$sm": (
        "SuspendDispatchSuperPlain",
        "token_dotkt_suspend",
        "token$dotkt_suspend",
        0,
    ),
    "SuspendDispatchSuperOverride_token$sm": (
        "SuspendDispatchSuperOverride",
        "token_dotkt_suspend",
        "token$dotkt_suspend",
        0,
    ),
    "SuspendDispatchSuperGeneric_callGeneric$sm": (
        "SuspendDispatchSuperGeneric",
        "echo_dotkt_suspend",
        "echo$dotkt_suspend",
        1,
    ),
}

for state_machine, (receiver_type, helper_suffix, cold_method, generic_arity) in targets.items():
    type_node = types.get(state_machine)
    if type_node is None:
        raise SystemExit(f"#436 CIR: missing state machine {state_machine}")
    helper_calls = [
        item
        for item in objects(type_node)
        if item.get("k") == "callInstance"
        and isinstance(item.get("method"), str)
        and item["method"].startswith("dotkt$super$")
        and item["method"].endswith(f"${helper_suffix}")
    ]
    if len(helper_calls) != 1:
        raise SystemExit(
            f"#436 CIR: {state_machine} has {len(helper_calls)} super helpers, expected exactly 1"
        )
    helper_call = helper_calls[0]
    receiver = helper_call.get("recv")
    expected_receiver_owner = {"t": "fqn", "name": receiver_type}
    if helper_call.get("ownerType") != expected_receiver_owner or helper_call.get("virtual") is not False:
        raise SystemExit(
            f"#436 CIR: {state_machine} does not call its super helper non-virtually: {helper_call!r}"
        )
    if not (
        isinstance(receiver, dict)
        and receiver.get("k") == "field"
        and receiver.get("ownerType") == {"t": "fqn", "name": state_machine}
        and receiver.get("name") == "$this"
        and receiver.get("ret") == expected_receiver_owner
    ):
        raise SystemExit(f"#436 CIR: {state_machine} has an unexpected receiver: {receiver!r}")

    receiver_node = types.get(receiver_type)
    helpers = [
        method
        for method in receiver_node.get("methods", [])
        if method.get("name") == helper_call["method"]
    ] if receiver_node else []
    if len(helpers) != 1:
        raise SystemExit(
            f"#436 CIR: {receiver_type} has {len(helpers)} matching super helpers, expected exactly 1"
        )
    helper_type_params = helpers[0].get("typeParams", [])
    if len(helper_type_params) != generic_arity:
        raise SystemExit(
            f"#436 CIR: {helper_call['method']} has {len(helper_type_params)} generic parameters, "
            f"expected {generic_arity}"
        )
    base_calls = [
        item
        for item in objects(helpers[0].get("body", []))
        if item.get("k") == "callInstance" and item.get("method") == cold_method
    ]
    if len(base_calls) != 1:
        raise SystemExit(
            f"#436 CIR: {helper_call['method']} has {len(base_calls)} base cold calls, expected exactly 1"
        )
    call = base_calls[0]
    owner = call.get("ownerType")
    expected_owner = {"t": "fqn", "name": "SuspendDispatchSuperBase"}
    if owner != expected_owner or call.get("virtual") is not False or call.get("super") is not True:
        raise SystemExit(
            f"#436 CIR: {helper_call['method']} lost base non-virtual dispatch: "
            f"owner={owner!r}, virtual={call.get('virtual')!r}, super={call.get('super')!r}"
        )
    if call.get("recv") != {"k": "this"}:
        raise SystemExit(f"#436 CIR: {helper_call['method']} has an unexpected base receiver: {call.get('recv')!r}")

print("#436 suspend super-call cold-entry CIR dispatch OK")
