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

methods = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "runtimeTypesFuse"
]
if len(methods) != 1:
    raise SystemExit(f"found {len(methods)} runtimeTypesFuse methods, expected 1")

calls = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name")
    == "RuntimeTypesExistentialFusibleFlow$star"
]
if len(calls) != 1:
    raise SystemExit(f"found {len(calls)} existential fuse calls, expected 1: {calls!r}")

call = calls[0]
physical = {"t": "fqn", "name": "RuntimeTypesExistentialFlow$star"}
if call.get("method") != "$star$fuse$0" or call.get("ret") != physical:
    raise SystemExit(f"existential call does not state the exact physical slot result: {call!r}")
if call.get("dynRet") != physical or call.get("virtual") is not True:
    raise SystemExit(f"existential call is not an exact virtual physical dispatch: {call!r}")

casts = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "cast" and node.get("e") is call
]
if len(casts) != 1:
    raise SystemExit(f"found {len(casts)} semantic projections around fuse, expected 1")

semantic = {
    "t": "fqn",
    "name": "RuntimeTypesExistentialFlow",
    "args": [{"t": "tv", "scope": "method", "i": 0}],
}
if casts[0].get("type") != semantic:
    raise SystemExit(f"fuse result is not projected to the caller's semantic type: {casts[0]!r}")

print("existential call result is physical and its caller-visible projection is explicit")
