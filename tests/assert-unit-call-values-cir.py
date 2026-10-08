#!/usr/bin/env python3
"""Unit value edges must contain an honest void statement and an exact singleton read."""
import json
import sys


def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from objects(child)


def unit_read(node):
    return (node.get("k") == "staticField" and node.get("name") == "INSTANCE"
            and node.get("ownerType") == {"t": "fqn", "name": "kotlin.Unit"})


lane, path = sys.argv[1:]
with open(path, encoding="utf-8") as stream:
    cir = json.load(stream)
methods = {node["name"]: node for node in objects(cir)
           if "name" in node and "params" in node and "body" in node}
blocks = [node for node in objects(cir)
          if node.get("k") == "valueBlock" and unit_read(node.get("result", {}))
          and node.get("type") == {"t": "fqn", "name": "kotlin.Unit"}]
assert blocks, "no Unit result materialization exercised"
for block in blocks:
    assert block["type"] == {"t": "fqn", "name": "kotlin.Unit"}, block
    assert len(block["stmts"]) == 1 and block["stmts"][0]["k"] == "exprStmt", block
    call = block["stmts"][0]["expr"]
    if call["k"] == "delegateInvoke":
        result = call["funcType"]["ret"]
    else:
        result = (call.get("memberRef", {}).get("returnType")
                  or methods.get(call.get("method"), {}).get("ret") or call.get("ret") or call.get("type"))
    assert result in ({"t": "fqn", "name": "void"}, {"t": "fqn", "name": "System.Void"}), call
    field = block["result"]["fieldRef"]
    assert field["kind"] == "field" and field["name"] == "INSTANCE", field
    assert field["declaringType"] == field["returnType"] == {"t": "fqn", "name": "kotlin.Unit"}, field

if lane == "basic":
    returned = methods["unitReturnedValue"]["body"][0]["value"]
    assert returned in blocks, returned
    discarded = methods["unitDiscardedCall"]["body"]
    assert not any(unit_read(node) for node in objects(discarded)), discarded
    assert sum(node.get("method") == "unitValueEffect" for node in objects(discarded)) == 1
    constrained = methods["unitConstrainedValue"]["body"]
    assert not any(unit_read(node) for node in objects(constrained)), constrained
    carrier = {"t": "fqn", "name": "UnitCallSource$star"}
    object_type = {"t": "fqn", "name": "object"}
    calls = [node for node in objects(constrained)
             if node.get("k") == "callInstance" and node.get("ownerType") == carrier]
    assert len(calls) == 1, calls
    call = calls[0]
    assert (call.get("method") == "$star$get$0" and call.get("virtual") is True
            and call.get("sig") == [] and call.get("args") == []
            and call.get("ret") == object_type), call
    projections = [node for node in objects(constrained)
                   if node.get("k") == "cast" and node.get("e") == call
                   and node.get("type") == {"t": "fqn", "name": "kotlin.Unit"}]
    assert len(projections) == 1, projections
    owners = [owner for owner in cir.get("types", []) if owner.get("name") == carrier["name"]]
    assert len(owners) == 1 and owners[0].get("kind") == "interface", owners
    entries = [entry for entry in owners[0].get("methods", []) if entry.get("name") == call["method"]]
    assert (len(entries) == 1 and entries[0].get("abstract") is True
            and entries[0].get("params") == [] and entries[0].get("ret") == object_type), entries
elif lane == "consumer":
    returned = methods["referencedUnitReturn"]["body"][0]["value"]
    assert returned in blocks, returned
    assert returned["stmts"][0]["expr"]["memberRef"]["returnType"]["name"] in ("void", "System.Void")
else:
    raise AssertionError(f"unknown fixture lane: {lane}")
print(f"{lane}: consumed Unit values use exact void calls and resolved singleton reads")
