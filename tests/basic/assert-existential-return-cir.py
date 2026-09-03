#!/usr/bin/env python3
"""Assert that existential calls and generated capture storage state exact physical carriers."""

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
        "usage: assert-existential-return-cir.py <RuntimeTypes...cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

physical = {"t": "fqn", "name": "RuntimeTypesExistentialFlow$star"}
semantic = {
    "t": "fqn",
    "name": "RuntimeTypesExistentialFlow",
    "args": [{"t": "tv", "scope": "method", "i": 0}],
}

for method_name in ("runtimeTypesFuse", "runtimeTypesFuseViaRealignedLocal"):
    methods = [
        method
        for method in root.get("methods", [])
        if isinstance(method, dict) and method.get("name") == method_name
    ]
    if len(methods) != 1:
        raise SystemExit(f"found {len(methods)} {method_name} methods, expected 1")

    calls = [
        node
        for node in objects(methods[0].get("body", []))
        if node.get("k") == "callInstance"
        and node.get("ownerType", {}).get("name")
        == "RuntimeTypesExistentialFusibleFlow$star"
    ]
    if len(calls) != 1:
        raise SystemExit(
            f"found {len(calls)} existential fuse calls in {method_name}, expected 1: {calls!r}"
        )

    call = calls[0]
    if call.get("method") != "$star$fuse$0" or call.get("ret") != physical:
        raise SystemExit(
            f"{method_name} does not state the exact physical slot result: {call!r}"
        )
    if call.get("dynRet") != physical or call.get("virtual") is not True:
        raise SystemExit(
            f"{method_name} is not an exact virtual physical dispatch: {call!r}"
        )

    casts = [
        node
        for node in objects(methods[0].get("body", []))
        if node.get("k") == "cast" and node.get("e") is call
    ]
    if len(casts) != 1:
        raise SystemExit(
            f"found {len(casts)} semantic projections in {method_name}, expected 1"
        )
    if casts[0].get("type") != semantic:
        raise SystemExit(
            f"{method_name} does not project to the caller's semantic type: {casts[0]!r}"
        )

reference_methods = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "runtimeTypesFuseReference"
]
if len(reference_methods) != 1:
    raise SystemExit(
        f"found {len(reference_methods)} runtimeTypesFuseReference methods, expected 1"
    )

carrier = {"t": "fqn", "name": "RuntimeTypesExistentialFusibleFlow$star"}
constructions = [
    node
    for node in objects(reference_methods[0].get("body", []))
    if node.get("k") == "newClosure"
    and len(node.get("captures", [])) == 1
    and node["captures"][0].get("type") == carrier
]
if len(constructions) != 1:
    raise SystemExit(
        f"found {len(constructions)} existential callable-reference closures, expected 1"
    )

closure_name = constructions[0].get("closureType", {}).get("name")
closures = [
    candidate
    for candidate in root.get("types", [])
    if isinstance(candidate, dict) and candidate.get("name") == closure_name
]
if len(closures) != 1:
    raise SystemExit(f"found {len(closures)} definitions for closure {closure_name!r}, expected 1")

closure = closures[0]
fields = closure.get("fields", [])
ctors = closure.get("ctors", [])
if (
    len(fields) != 1
    or fields[0].get("name") != "__recv"
    or fields[0].get("type") != carrier
    or len(ctors) != 1
    or len(ctors[0].get("params", [])) != 1
    or ctors[0]["params"][0].get("type") != carrier
):
    raise SystemExit(
        f"callable-reference capture, constructor, and field do not share the carrier: {closure!r}"
    )

invokes = [method for method in closure.get("methods", []) if method.get("name") == "invoke"]
if len(invokes) != 1:
    raise SystemExit(f"found {len(invokes)} invoke methods on {closure_name}, expected 1")

closure_semantic = {
    "t": "fqn",
    "name": "RuntimeTypesExistentialFlow",
    "args": [{"t": "tv", "scope": "type", "i": 0}],
}
invoke = invokes[0]
returns = [node for node in invoke.get("body", []) if node.get("k") == "return"]
if len(returns) != 1 or returns[0].get("value", {}).get("k") != "cast":
    raise SystemExit(f"{closure_name}.invoke does not explicitly project its result: {invoke!r}")
projection = returns[0]["value"]
call = projection.get("e", {})
if (
    invoke.get("ret") != closure_semantic
    or projection.get("type") != closure_semantic
    or call.get("k") != "callInstance"
    or call.get("ownerType") != carrier
    or call.get("method") != "$star$fuse$0"
    or call.get("ret") != physical
    or call.get("recv", {}).get("k") != "field"
    or call["recv"].get("name") != "__recv"
):
    raise SystemExit(
        f"{closure_name}.invoke does not call the physical slot and project to its semantic return: {invoke!r}"
    )

sam_factories = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "runtimeTypesFuseSam"
]
if len(sam_factories) != 1:
    raise SystemExit(f"found {len(sam_factories)} runtimeTypesFuseSam methods, expected 1")
sam_news = [node for node in objects(sam_factories[0].get("body", [])) if node.get("k") == "newSam"]
if len(sam_news) != 1:
    raise SystemExit(f"found {len(sam_news)} existential SAM constructions, expected 1")
sam_name = sam_news[0].get("samType", {}).get("name")
sams = [candidate for candidate in root.get("types", []) if candidate.get("name") == sam_name]
if len(sams) != 1:
    raise SystemExit(f"found {len(sams)} definitions for SAM {sam_name!r}, expected 1")
sam = sams[0]
if (
    len(sam.get("fields", [])) != 1
    or sam["fields"][0].get("type") != carrier
    or len(sam.get("ctors", [])) != 1
    or sam["ctors"][0].get("params", [{}])[0].get("type") != carrier
):
    raise SystemExit(f"SAM capture, constructor, and field do not share the carrier: {sam!r}")
sam_methods = [method for method in sam.get("methods", []) if method.get("name") == "fuse"]
if len(sam_methods) != 1:
    raise SystemExit(f"found {len(sam_methods)} fuse methods on {sam_name!r}, expected 1")
sam_returns = [node for node in sam_methods[0].get("body", []) if node.get("k") == "return"]
sam_projection = sam_returns[0].get("value", {}) if len(sam_returns) == 1 else {}
sam_call = sam_projection.get("e", {})
if (
    sam_projection.get("k") != "cast"
    or sam_projection.get("type") != closure_semantic
    or sam_call.get("ownerType") != carrier
    or sam_call.get("method") != "$star$fuse$0"
    or sam_call.get("recv", {}).get("name") != "fusible"
):
    raise SystemExit(f"{sam_name}.fuse does not project the physical carrier call: {sam_methods[0]!r}")

suspend_factories = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "runtimeTypesFuseSuspend"
]
if len(suspend_factories) != 1:
    raise SystemExit(
        f"found {len(suspend_factories)} runtimeTypesFuseSuspend methods, expected 1"
    )
suspend_news = [
    node
    for node in objects(suspend_factories[0].get("body", []))
    if node.get("k") == "new"
    and "runtimeTypesFuseSuspend_lambda" in node.get("type", {}).get("name", "")
]
if len(suspend_news) != 1 or suspend_news[0].get("argTypes", [None])[0] != carrier:
    raise SystemExit(f"suspend-lambda construction does not take the carrier: {suspend_news!r}")
sm_name = suspend_news[0]["type"]["name"]
state_machines = [candidate for candidate in root.get("types", []) if candidate.get("name") == sm_name]
if len(state_machines) != 1:
    raise SystemExit(f"found {len(state_machines)} definitions for state machine {sm_name!r}, expected 1")
state_machine = state_machines[0]
capture_fields = [field for field in state_machine.get("fields", []) if field.get("name") == "cap$fusible"]
if (
    len(capture_fields) != 1
    or capture_fields[0].get("type") != carrier
    or state_machine.get("ctors", [{}])[0].get("params", [{}])[0].get("type") != carrier
):
    raise SystemExit(
        f"suspend-lambda capture, constructor, and field do not share the carrier: {state_machine!r}"
    )
invoke_suspends = [
    method for method in state_machine.get("methods", []) if method.get("name") == "invokeSuspend"
]
sm_calls = [
    node
    for node in objects(invoke_suspends[0].get("body", []) if len(invoke_suspends) == 1 else [])
    if node.get("k") == "callInstance" and node.get("method") == "$star$fuse$0"
]
if (
    len(sm_calls) != 1
    or sm_calls[0].get("ownerType") != carrier
    or sm_calls[0].get("recv", {}).get("name") != "cap$fusible"
):
    raise SystemExit(f"{sm_name}.invokeSuspend does not call through the carrier: {invoke_suspends!r}")
sm_projections = [
    node
    for node in objects(invoke_suspends[0].get("body", []))
    if node.get("k") == "cast" and node.get("e") is sm_calls[0]
]
if len(sm_projections) != 1 or sm_projections[0].get("type") != closure_semantic:
    raise SystemExit(
        f"{sm_name}.invokeSuspend does not explicitly project its carrier result: {invoke_suspends[0]!r}"
    )

print(
    "existential calls and generated capture storage state physical carriers with explicit semantic projections"
)
