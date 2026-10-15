#!/usr/bin/env python3
"""Local extension reads name their authored receiver, independently of leading captures."""
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
    bir = json.load(stream)
locals_by_name = {
    node["decl"]["sourceName"]: node["decl"]
    for node in objects(bir) if node.get("k") == "localFun"
}

for name in ("replace", "replaceGeneric", "replacePair", "inner", "replaceWithOwners",
             "replaceMultiple", "replaceContext", "replaceRecursive", "noCapture"):
    declaration = locals_by_name[name]
    assert declaration["static"], declaration
    params = declaration["params"]
    names = [param["name"] for param in params]
    assert len(names) == len(set(names)), (name, names)
    assert "<this>" not in names, (name, names)
    # These bodies have no instance object/lambda body of their own. Every receiver is either the
    # declaration's own argument or a separately captured outer declaration, never the emitted `this`.
    assert not any(node.get("k") == "this" for node in objects(declaration["body"])), name

plain = locals_by_name["replace"]
capture, receiver = plain["params"]
assert "sharedCellType" in capture, capture
assert receiver["type"] == {"t": "fqn", "name": "kotlin.String"}, receiver
length = next(node for node in objects(plain["body"]) if node.get("method") == "length")
assert length["recv"]["k"] == "local" and length["recv"]["name"] == receiver["name"], length

generic = locals_by_name["replaceGeneric"]
assert len(generic["params"]) == 2, generic
assignment = next(node for node in objects(generic["body"])
                  if node.get("k") == "setLocal" or (node.get("k") == "setField" and node.get("name") == "v"))
assert assignment["value"]["k"] == "local" and assignment["value"]["name"] == generic["params"][1]["name"], assignment

context = locals_by_name["replaceContext"]
assert len(context["params"]) == 4, context
assert [param["type"]["name"] for param in context["params"][1:]] == [
    "kotlin.Int", "kotlin.String", "kotlin.Int"
], context
length = next(node for node in objects(context["body"]) if node.get("method") == "length")
assert length["recv"]["k"] == "local" and length["recv"]["name"] == context["params"][2]["name"], length

reference = next(node for node in objects(bir)
                 if node.get("k") == "localFun" and node["decl"]["sourceName"] == "replaceReference")
forwarders = []
for node in objects(bir):
    if node.get("k") != "newClosure":
        continue
    calls = [call for call in objects(node["synthClass"]["body"])
             if call.get("k") == "callLocal" and call["id"] == reference["id"]]
    if not calls:
        continue
    assert len(calls) == 1, calls
    call = calls[0]
    assert len(call["args"]) == len(call["sig"]) == len(reference["decl"]["params"]), call
    params = node["synthClass"]["params"]
    remaining = list(filter(None, [node["funcType"].get("recv")])) + node["funcType"]["params"]
    assert [param["type"] for param in params] == remaining, node
    if params:
        assert call["args"][-1] == {"k": "local", "name": params[0]["name"]}, call
    else:
        assert call["args"][-1]["k"] == "field", call
        assert len(node["captures"]) == 2, node
    forwarders.append(len(params))
assert sorted(forwarders) == [0, 0, 1], forwarders
print("Local extension receiver identities survive captures, generic frames and context arguments")
