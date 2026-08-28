#!/usr/bin/env python3
"""Assert #439's packaged cross-module suspend-super forwarding contract."""

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
    raise SystemExit("usage: assert-cross-module-suspend-super-cir.py <Main.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

derived_name = "asyncconsumer.CrossModuleSuspendDerived"
state_machine_name = derived_name + "_token$sm"
base_name = "asyncgate.CrossModuleSuspendBase"
types = {item.get("name"): item for item in root.get("types", []) if isinstance(item, dict)}

derived = types.get(derived_name)
state_machine = types.get(state_machine_name)
if derived is None or state_machine is None:
    raise SystemExit(
        f"#439 CIR: missing derived/state-machine types: derived={derived is not None}, "
        f"state_machine={state_machine is not None}"
    )

helper_calls = [
    item
    for item in objects(state_machine.get("methods", []))
    if item.get("k") == "callInstance"
    and isinstance(item.get("method"), str)
    and item["method"].startswith("dotkt$super$")
    and item["method"].endswith("$token_dotkt_suspend")
]
if len(helper_calls) != 1:
    raise SystemExit(f"#439 CIR: state machine has {len(helper_calls)} super-helper calls, expected 1")

helper_call = helper_calls[0]
derived_type = {"t": "fqn", "name": derived_name}
expected_receiver = {
    "k": "field",
    "ownerType": {"t": "fqn", "name": state_machine_name},
    "recv": {"k": "this"},
    "name": "$this",
    "ret": derived_type,
}
if (
    helper_call.get("ownerType") != derived_type
    or helper_call.get("virtual") is not False
    or helper_call.get("recv") != expected_receiver
):
    raise SystemExit(f"#439 CIR: state machine does not call its derived helper correctly: {helper_call!r}")

# The state machine must never issue the base cold call on its captured outer receiver directly. CLR verification
# requires that lexical base call to execute in an instance method whose physical `this` is the derived type.
direct_base_calls = [
    item
    for item in objects(state_machine.get("methods", []))
    if item.get("k") in ("callInstance", "clrInstance")
    and item.get("method") == "token$dotkt_suspend"
    and (item.get("ownerType") or item.get("type")) == {"t": "fqn", "name": base_name}
]
if direct_base_calls:
    raise SystemExit(f"#439 CIR: state machine calls the producer base cold entry directly: {direct_base_calls!r}")

helpers = [method for method in derived.get("methods", []) if method.get("name") == helper_call["method"]]
if len(helpers) != 1:
    raise SystemExit(f"#439 CIR: derived type has {len(helpers)} matching super helpers, expected 1")

base_calls = [
    item
    for item in objects(helpers[0].get("body", []))
    if item.get("k") in ("callInstance", "clrInstance") and item.get("method") == "token$dotkt_suspend"
]
if len(base_calls) != 1:
    raise SystemExit(f"#439 CIR: helper has {len(base_calls)} base cold calls, expected 1")

base_call = base_calls[0]
member_ref = base_call.get("memberRef")
if (
    (base_call.get("ownerType") or base_call.get("type")) != {"t": "fqn", "name": base_name}
    or base_call.get("recv") != {"k": "this"}
    or base_call.get("virtual") is not False
    or base_call.get("super") is not True
    or not isinstance(member_ref, dict)
    or member_ref.get("assembly") != "DotKt.AsyncGate"
    or member_ref.get("declaringType") != {"t": "fqn", "name": base_name}
    or member_ref.get("name") != "token$dotkt_suspend"
):
    raise SystemExit(f"#439 CIR: helper lost the exact non-virtual producer cold-entry edge: {base_call!r}")

print("#439 packaged cross-module suspend-super CIR forwarding OK")
