#!/usr/bin/env python3
"""Exact selected top-level identities govern deferred unchecked generic results."""
import base64
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


def load(path):
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


bir, reader, cir = map(load, sys.argv[1:])
source = {method["name"]: method for method in bir["methods"] if method["name"] != "overloaded"}
physical = {method["name"]: method for method in cir["methods"]}
readers = [method for method in reader["methods"] if method["name"] == "readOrdinaryTop"]
assert len(readers) == 1, readers
overloads = [method for method in bir["methods"] if method["name"] == "overloaded"]
assert len(overloads) == 2, overloads
erased_overload = next(method for method in overloads if method["params"][0]["type"]["t"] == "nullable")
typed_overload = next(method for method in overloads if method != erased_overload)
assert erased_overload["declarationId"] != typed_overload["declarationId"]

for caller, selected, owner, erased in (
    ("deferred", source["read"], bir["fileClass"], True),
    ("ordinary", readers[0], reader["fileClass"], True),
    ("deferredOverload", erased_overload, bir["fileClass"], True),
    ("ordinaryOverload", typed_overload, bir["fileClass"], False),
    ("forwarded", source["read"], bir["fileClass"], True),
):
    declarations = [node for node in objects(source[caller]["body"]) if node.get("k") == "var"]
    assert len(declarations) == 1, (caller, declarations)
    call = declarations[0]["init"]
    assert call["declarationId"] == selected["declarationId"], (caller, call)
    assert call["calleeOwner"]["name"] == owner, (caller, call)
    lowered = [node for node in objects(physical[caller]["body"]) if node.get("k") == "var"]
    assert len(lowered) == 1, (caller, lowered)
    local = lowered[0]
    assert (local["type"].get("name") == "object") == erased, (caller, local)
    if erased:
        assert local["init"]["ret"] == {"t": "fqn", "name": "object"}, (caller, local)
    else:
        assert local["type"]["t"] == "tv", local

concrete_read = [node for node in objects(source["concrete"]["body"])
                 if node.get("k") == "callStatic" and node.get("method") == "read"]
assert len(concrete_read) == 1, concrete_read
assert concrete_read[0]["declarationId"] == source["read"]["declarationId"]
physical_read = [node for node in objects(physical["concrete"]["body"])
                 if node.get("k") == "callStatic" and node.get("method") == "read"]
assert len(physical_read) == 1, physical_read
assert physical["read"]["ret"] == {"t": "fqn", "name": "object"}, physical["read"]
assert physical_read[0]["ret"] == {"t": "fqn", "name": "System.Int32"}, physical_read

source_forward = [node for node in objects(source["forwarded"]["body"])
                  if node.get("k") == "callStatic" and node.get("method") == "identity"]
assert len(source_forward) == 1, source_forward
assert source_forward[0]["declarationId"] == source["identity"]["declarationId"]
forward_calls = [node for node in objects(physical["forwarded"]["body"])
                 if node.get("k") == "callStatic" and node.get("method") == "identity"]
assert len(forward_calls) == 1, forward_calls
forward_call = forward_calls[0]
parameter = physical["identity"]["params"][0]["type"]
assert parameter["t"] == "tv" and parameter["scope"] == "method", parameter
expected = forward_call["typeArgs"][parameter["i"]]
assert forward_call["args"][0]["k"] == "cast", forward_call
assert forward_call["args"][0]["type"] == expected, forward_call
assert forward_call["args"][0]["e"]["k"] == "local", forward_call
assert forward_call["ret"] == expected == physical["forwarded"]["ret"], forward_call

static_source = next(node for node in objects(source["deferredStatic"]["body"]) if node.get("k") == "var")
selected_static = static_source["init"]
assert selected_static["k"] == "callStatic", selected_static
source_owners = [owner for owner in bir["types"]
                 if any(method.get("declarationId") == selected_static["declarationId"]
                        for method in owner.get("methods", []))]
assert len(source_owners) == 1 and len(source_owners[0]["typeParams"]) == 1, source_owners
assert selected_static["ownerType"]["name"] == source_owners[0]["name"], selected_static
source_static = next(method for method in source_owners[0]["methods"]
                     if method["declarationId"] == selected_static["declarationId"])
assert source_static["static"] and source_static["kotlinStatic"], source_static
static_local = next(node for node in objects(physical["deferredStatic"]["body"]) if node.get("k") == "var")
assert static_local["type"] == {"t": "fqn", "name": "object"}, static_local
static_call = static_local["init"]
assert static_call["k"] == "callStatic" and "recv" not in static_call, static_call
assert static_call["ret"] == {"t": "fqn", "name": "object"}, static_call
carriers = [owner for owner in cir["types"] if owner["name"] == static_call["owner"]["name"]]
assert len(carriers) == 1 and not carriers[0].get("typeParams"), carriers
carrier = carriers[0]
relations = [attr for attr in carrier["attrs"]
             if attr["attr"]["name"] == "DotKt.Runtime.CompilerServices.KotlinStaticCarrierAttribute"]
assert len(relations) == 1, carrier
assert relations[0]["args"][0]["value"] == "bir-json/1", relations
relation = json.loads(base64.b64decode(relations[0]["args"][1]["bytes"]))
assert relation["owner"] == source_owners[0]["name"], relation
physical_static = [method for method in carrier["methods"] if method["name"] == static_call["method"]]
assert len(physical_static) == 1 and physical_static[0]["static"], physical_static
assert physical_static[0]["ret"] == static_call["ret"], physical_static
print("Selected top-level/static identities, generic forwarding frames, erased storage and concrete checks: OK")
