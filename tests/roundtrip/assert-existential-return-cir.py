#!/usr/bin/env python3
"""Assert that a referenced existential member keeps physical and semantic results distinct."""

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
    raise SystemExit("usage: assert-existential-return-cir.py <CrossModuleMetadataTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

methods = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "fuseReferencedExistential"
]
if len(methods) != 1:
    raise SystemExit(f"found {len(methods)} fuseReferencedExistential methods, expected 1")

calls = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name")
    == "starprojection.ReferencedExistentialFusibleFlow$star"
]
if len(calls) != 1:
    raise SystemExit(f"found {len(calls)} referenced existential fuse calls, expected 1: {calls!r}")

call = calls[0]
physical = {"t": "fqn", "name": "starprojection.ReferencedExistentialFlow$star"}
if call.get("method") != "$star$fuse$0" or call.get("ret") != physical:
    raise SystemExit(f"referenced call does not state the physical slot result: {call!r}")
member_ref = call.get("memberRef", {})
if (
    call.get("virtual") is not True
    or member_ref.get("declaringType") != call.get("ownerType")
    or member_ref.get("returnType") != physical
):
    raise SystemExit(f"referenced call/memberRef disagree with the physical slot: {call!r}")

casts = [
    node
    for node in objects(methods[0].get("body", []))
    if node.get("k") == "cast" and node.get("e") is call
]
if len(casts) != 1:
    raise SystemExit(f"found {len(casts)} semantic projections around referenced fuse, expected 1")

semantic = {
    "t": "fqn",
    "name": "starprojection.ReferencedExistentialFlow`1",
    "args": [{"t": "tv", "scope": "method", "i": 0}],
}
if casts[0].get("type") != semantic:
    raise SystemExit(f"referenced result is not projected to the consumer's semantic type: {casts[0]!r}")

print("referenced existential call result is physical and its consumer-visible projection is explicit")
