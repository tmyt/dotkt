#!/usr/bin/env python3
"""A same-named generic slot consumes the selected source ID without reopening its signature."""
import base64
import json
import sys


def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)


def payloads(method, name):
    return [json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
            for attribute in method.get("attrs", [])
            if attribute.get("attr", {}).get("name")
            == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


producer, source, consumer = [json.load(open(path, encoding="utf-8")) for path in sys.argv[1:]]
owner = "roundtrip.capturedgeneric.CapturedContextReader"
selected = [node for node in nodes(source) if node.get("k") == "callInstance"
            and node.get("ownerType", {}).get("name") == owner + "`2"]
assert len(selected) == 1 and len(selected[0]["typeArgs"]) == 1
identity = selected[0]["declarationId"]
declaration = next(item for item in producer["types"] if item["name"] == owner)
method = next(item for item in declaration["methods"]
              if any(fact.get("id") == identity for fact in payloads(item, "KotlinDeclarationIdentity")))
facts = payloads(method, "KotlinDeclarationIdentity")[0]
assert facts["nullableFrame"]["sourceArity"] == 1
assert facts["signature"]["params"][2] == {"t": "tv", "scope": "method", "i": 0}
assert facts["signature"]["params"][1]["name"].replace("+", ".") == "kotlin.coroutines.CoroutineContext.Element"
carrier = next(item for item in producer["types"] if item["name"] == owner + "$star")
slots = [item for item in carrier["methods"]
         if any(fact.get("sourceDeclaration") == identity for fact in payloads(item, "KotlinSourceMethod"))]
assert len(slots) == 1
slot = slots[0]
assert slot["name"] == method["name"] == "read" and len(slot["typeParams"]) == 2
assert [parameter["type"] for parameter in slot["params"]][-1] == {"t": "tv", "scope": "method", "i": 1}
assert payloads(slot["params"][2], "KotlinNullableGeneric") == [{"t": "tv", "scope": "method", "i": 0}]
calls = [node for node in nodes(consumer) if node.get("k") == "callInstance"
         and node.get("ownerType", {}).get("name") == carrier["name"] and node.get("method") == slot["name"]]
assert len(calls) == 1
call = calls[0]
assert call["memberRef"]["declaringType"] == call["ownerType"]
assert call["memberRef"]["genericArity"] == len(call["typeArgs"]) == 2
assert call["memberRef"]["parameterTypes"] == call["calleeParams"] == [parameter["type"] for parameter in slot["params"]]
assert call["memberRef"]["parameterTypes"][-1] == {"t": "tv", "scope": "method", "i": 1}
selected_descriptor = {key: call.get(key) for key in
                       ("ownerType", "memberRef", "sig", "calleeParams", "typeArgs", "ret", "dynRet", "sty")}
assert not any(node.get("t") == "tv" and node.get("scope") == "type" for node in nodes(selected_descriptor))
construction = call["recv"]
assert construction["k"] == "new" and construction["type"] == {
    "t": "fqn", "name": owner + "`2", "args": [{"t": "fqn", "name": "System.Int32"}] * 2}
assert construction["memberRef"]["declaringType"] == construction["type"]
assert construction["memberRef"]["parameterTypes"] == [{"t": "tv", "scope": "type", "i": 1}]
print("same-named generic carrier calls retain exact source association and independent method frames")
