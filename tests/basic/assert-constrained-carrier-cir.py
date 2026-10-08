#!/usr/bin/env python3
"""Verify source-bound metadata and direct physical forwarding independently."""
import base64
import json
import sys
from pathlib import Path


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

# The wide generic slot forces CLR interface dispatch to resolve modifier tokens;
# a bare method-variable TypeSpec can pass ILVerify yet fail when this is invoked.
with open(Path(sys.argv[1]).parent / "000-dotkt-parameter-signatures.cir.json", encoding="utf-8") as stream:
    marker_definitions = {t["name"]: t for t in json.load(stream)["types"]}
wide_owner = next(t for t in cir["types"] if t["name"] == "OwnerBoundWide")
wide_source = next(m for m in wide_owner["methods"] if m["name"] == "accept")
wide_bridge = next(m for m in wide_owner["methods"] if m.get("clrInterfaceImpls"))
descriptor, = wide_bridge["clrInterfaceImpls"]
wide_carrier = next(t for t in cir["types"] if t["name"] == descriptor["owner"]["name"])
wide_slot = next(m for m in wide_carrier["methods"] if m["name"] == descriptor["member"])
signature = wide_source["params"][0]["type"]
assert signature["t"] == "mod" and signature["req"] is False, signature
assert signature["of"] == {"t": "fqn", "name": "object"}, signature
marker = signature["m"]
assert marker["t"] == "fqn" and not marker.get("args"), marker
definition = marker_definitions[marker["name"]]
assert definition["generated"] and definition["abstract"] and definition["vis"] == "public", definition
assert not definition["typeParams"] and not definition["methods"], definition
for declaration in (wide_source, wide_bridge, wide_slot):
    assert len(declaration["params"]) == 23, declaration["name"]
    assert declaration["params"][0]["type"] == signature, declaration["name"]
    attribute, = [a for a in declaration["params"][0].get("attrs", []) if a["attr"]["name"] ==
                   "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"]
    assert json.loads(base64.b64decode(attribute["args"][1]["bytes"])) == {
        "t": "tv", "scope": "method", "i": 0}, declaration["name"]
assert descriptor["params"] == [p["type"] for p in wide_slot["params"]], descriptor
wide_call, = [n for n in objects(wide_bridge["body"]) if n.get("k") == "callInstance"]
assert wide_call["calleeParams"] == [p["type"] for p in wide_source["params"]], wide_call
assert wide_call["typeArgs"] == [{"t": "tv", "scope": "method", "i": 0}], wide_call
print("source constraint metadata and direct carrier dispatch are exact")
