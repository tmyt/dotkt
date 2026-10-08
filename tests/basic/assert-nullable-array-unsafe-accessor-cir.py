#!/usr/bin/env python3
"""Assert source nullable-array frames, exact accessor slots and concrete use projections."""

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


if len(sys.argv) != 2:
    raise SystemExit("usage: assert-nullable-array-unsafe-accessor-cir.py <NullableTests.cir.json>")
with open(sys.argv[1], encoding="utf-8") as handle:
    root = json.load(handle)

string_type = {"t": "fqn", "name": "System.String"}
string_array = {"t": "array", "elem": string_type}
physical_array = {"t": "fqn", "name": "System.Array"}


def one(items, description):
    if len(items) != 1:
        raise SystemExit(f"expected one {description}, found {len(items)}")
    return items[0]


def source_result(declaration, scope):
    carrier = one([attribute for attribute in declaration.get("retAttrs", [])
                   if attribute.get("attr", {}).get("name")
                   == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"], "source result carrier")
    expected = {"t": "nullable", "of": {"t": "array", "elem": {
        "t": "nullable", "of": {"t": "tv", "scope": scope, "i": 0}}}}
    args = carrier.get("args", [])
    if (len(args) != 2 or args[0].get("value") != "bir-json/1"
            or json.loads(base64.b64decode(args[1]["bytes"], validate=True)) != expected):
        raise SystemExit(f"nullable array lost its source {scope} T frame")


def target_name(declaration):
    return [argument.get("value", {}).get("value")
            for attribute in declaration.get("attrs", [])
            if attribute.get("attr", {}).get("name") == "System.Runtime.CompilerServices.UnsafeAccessorAttribute"
            for argument in attribute.get("namedArgs", []) if argument.get("name") == "Name"]


def owned_accessor(call):
    host = one([owner for owner in root["types"]
                if owner.get("name") == call["owner"]["name"]], "accessor owner")
    wrapper = one([method for method in host["methods"]
                   if method.get("name") == call.get("method")], "owned accessor wrapper")
    extern = one([method for method in host["methods"]
                  if method.get("name") == wrapper["name"].removesuffix("$invoke")
                  and method.get("extern") is True], "owned accessor extern")
    expected_target = {"t": "fqn", "name": "NgProtectedArrayBase", "args": [
        {"t": "tv", "scope": "type", "i": 0}, {"t": "tv", "scope": "type", "i": 1}]}
    if (len(host.get("typeParams", [])) != 2 or call.get("ret") != physical_array
            or call["owner"].get("args") != [string_type, string_type]
            or call.get("sig") != [expected_target] or len(call.get("args", [])) != 1
            or target_name(extern) != ["prop_get<values>"]):
        raise SystemExit("owned nullable-array call lost its exact target or complete closed owner frame")
    for declaration in (wrapper, extern):
        if (declaration.get("ret") != physical_array
                or [parameter.get("type") for parameter in declaration.get("params", [])] != [expected_target]):
            raise SystemExit("owned nullable-array declaration lost its exact opaque array signature")
        source_result(declaration, "type")


derived = one([owner for owner in root["types"] if owner.get("name") == "NgProtectedArrayText"], "derived owner")
snapshot = one([method for method in derived["methods"] if method.get("name") == "snapshot"], "snapshot method")
calls = [node for node in objects(snapshot.get("body", []))
         if node.get("k") == "callStatic" and node.get("owner", {}).get("name") == "NgProtectedArrayBase"
         and node.get("method", "").endswith("$invoke")]
call = one(calls, "snapshot accessor call")
owned_accessor(call)
one([node for node in objects(snapshot["body"])
     if node.get("k") == "cast" and node.get("type") == string_array and node.get("e") is call],
    "concrete snapshot use projection")

stores = [node for node in objects(root["types"])
          if node.get("k") == "setField" and node.get("name") == "v"
          and node.get("ownerType", {}).get("name", "").startswith("dotkt$NullableTestsKt$Ref$")
          and node.get("value", {}).get("k") == "cast" and node["value"].get("type") == string_array
          and node["value"].get("e", {}).get("k") == "callStatic"]
owner_store = one([store for store in stores
                   if store["value"]["e"].get("owner", {}).get("name") == "NgProtectedArrayBase"],
                  "captured owner-array store projection")
owned_accessor(owner_store["value"]["e"])

closure = one([owner for owner in root["types"] if owner.get("nestedIn") == "NgProtectedMethodText"],
              "method-array closure")
extern = one([method for method in closure["methods"]
              if method.get("extern") is True and target_name(method) == ["pick"]], "method-array accessor")
expected_params = [{"t": "fqn", "name": "NgProtectedMethodBase"}, physical_array]
if (len(extern.get("typeParams", [])) != 2 or extern.get("ret") != physical_array
        or [parameter.get("type") for parameter in extern.get("params", [])] != expected_params):
    raise SystemExit("method-array accessor lost its two-variable frame or exact opaque signature")
source_result(extern, "method")
store = one([store for store in stores
             if store["value"]["e"].get("owner", {}).get("name") == closure["name"]
             and store["value"]["e"].get("method") == extern["name"]], "captured method-array store projection")
call = store["value"]["e"]
if (call.get("typeArgs") != [string_type, string_type] or call.get("ret") != physical_array
        or call.get("sig") != expected_params or len(call.get("args", [])) != 2):
    raise SystemExit("method-array consumer lost its exact constructed accessor frame")

print("nullable-array accessors preserve owner/method source frames, opaque slots and concrete store projections")
