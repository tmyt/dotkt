#!/usr/bin/env python3
"""Assert that #640 states the physical existential result and semantic projection separately."""

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

print(
    "existential calls and callable-reference storage state physical carriers with explicit semantic projections"
)
