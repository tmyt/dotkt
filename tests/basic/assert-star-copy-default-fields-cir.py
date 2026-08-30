#!/usr/bin/env python3
"""Assert that #621 projects star-receiver copy defaults through existential getter slots."""

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
        "usage: assert-star-copy-default-fields-cir.py <DefaultArgumentTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

methods = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "defaultArgCopyThroughStar"
]
if len(methods) != 1:
    raise SystemExit(f"CIR: found {len(methods)} star-copy helpers, expected 1")

body = methods[0].get("body", [])
star_owner = "DefaultArgStarCopy$star"
star_fields = [
    node
    for node in objects(body)
    if node.get("k") == "field"
    and isinstance(node.get("ownerType"), dict)
    and node["ownerType"].get("name") == star_owner
]
if star_fields:
    raise SystemExit(
        "CIR: fieldless existential owner still receives direct field reads: "
        f"{[node.get('name') for node in star_fields]!r}"
    )

default_reads = [
    node
    for node in objects(body)
    if node.get("k") == "callInstance"
    and isinstance(node.get("ownerType"), dict)
    and node["ownerType"].get("name") == star_owner
    and isinstance(node.get("recv"), dict)
    and node["recv"].get("k") == "local"
    and node["recv"].get("name") == "source"
    and node.get("sig") == []
]
returns = sorted(
    node.get("ret", {}).get("name")
    for node in default_reads
    if isinstance(node.get("ret"), dict)
)
if returns != ["System.Object", "System.String"] or not all(
    node.get("virtual") is True for node in default_reads
):
    raise SystemExit(
        "CIR: copy defaults must be two virtual existential getter calls returning object/string: "
        f"{default_reads!r}"
    )

print("star-projected copy defaults use existential getter slots")
