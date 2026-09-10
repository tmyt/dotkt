#!/usr/bin/env python3
"""Verify source-bound metadata and direct physical forwarding independently."""
import base64
import json
import sys


def objects(node):
    if isinstance(node, dict):
        yield node
        for child in node.values():
            yield from objects(child)
    elif isinstance(node, list):
        for child in node:
            yield from objects(child)


with open(sys.argv[1], encoding="utf-8") as stream:
    cir = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    bir = json.load(stream)
owner = next(t for t in cir["types"] if t["name"] == "OwnerBoundSink")
carrier = next(t for t in cir["types"] if t["name"] == "OwnerBoundSink$star")
source_owner = next(t for t in bir["types"] if t["name"] == "OwnerBoundSink")
for name in ("render", "echo", "withCallback", "transitive", "update", "fail", "record"):
    original = next(m for m in source_owner["methods"] if m["name"] == name)
    source = next(m for m in owner["methods"] if m["name"] == name)
    attribute = next(a for a in source["attrs"] if a["attr"]["name"] ==
                     "DotKt.Runtime.CompilerServices.KotlinTypeParameterBoundsAttribute")
    payload = json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
    expected = {str(i): p["constraints"] for i, p in enumerate(original["typeParams"])
                if isinstance(p, dict) and "constraints" in p}
    assert payload["bounds"] == expected, (name, payload, expected)
    candidates = [m for m in owner["methods"] if m.get("clrInterfaceImpls") and
                  any(n.get("k") == "callInstance" and n.get("method") == name
                      for n in objects(m.get("body", [])))]
    assert len(candidates) == 1, (name, len(candidates))
    bridge = candidates[0]
    assert bridge["vis"] == "private", name
    slot = next(m for m in carrier["methods"] if m["name"] == bridge["name"])
    assert slot["typeParams"] == bridge["typeParams"], name
    for declaration in (source, bridge, slot):
        assert not any(n.get("t") == "tv" and n.get("scope") == "type"
                       for n in objects(declaration["typeParams"])), name
    call = next(n for n in objects(bridge["body"]) if n.get("k") == "callInstance")
    assert call["typeArgs"] == [{"t": "tv", "scope": "method", "i": i}
                                for i in range(len(source["typeParams"]))], name
assert not any("_ownerConstraintDispatchBounds" in node or "_foreignDeclarationSignature" in node
               for node in objects(cir))
print("source constraint metadata and direct carrier dispatch are exact")
