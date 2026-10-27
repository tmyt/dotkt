#!/usr/bin/env python3
"""A source/native shared implementation forwards to its selected physical declaration, not itself."""
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


def facts(method, name):
    return [json.loads(base64.b64decode(attr["args"][1]["bytes"])) for attr in method.get("attrs", [])
            if attr.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


root = json.load(open(sys.argv[1], encoding="utf-8"))
owner = next(item for item in root["types"] if item["name"] == "roundtriptests.parameterslots.SharedReceiver")
implementation = next(method for method in owner["methods"] if facts(method, "KotlinDeclarationIdentity"))
identity = facts(implementation, "KotlinDeclarationIdentity")[0]
assert identity["name"] == implementation["name"]
assert implementation["vis"] == "public" and implementation["virtual"]
physical = [parameter["type"] for parameter in implementation["params"]]
assert physical == [{"t": "fqn", "name": "roundtrip.genericvalueinterop.Box`2", "args": [
    {"t": "fqn", "name": "System.String"}, {"t": "fqn", "name": "System.String"}]}]
bridges = [method for method in owner["methods"] if method.get("clrInterfaceImpls")]
assert len(bridges) == 1
bridge = bridges[0]
assert bridge["vis"] == "private" and bridge["virtual"]
assert [p["type"] for p in bridge["params"]] == [{"t": "fqn", "name": "roundtrip.genericvalueinterop.Box$star"}]
call, = [item for item in nodes(bridge["body"]) if item.get("k") == "callInstance"]
# Source identity has already been consumed by binding. The final call must select this unique physical
# declaration, not the separately named bridge or the inherited carrier-signature overload.
assert call["ownerType"] == {"t": "fqn", "name": owner["name"]}
assert call["method"] == implementation["name"] and call["method"] != bridge["name"]
assert call["sig"] == call["calleeParams"] == physical
assert call["args"] == [{"k": "cast", "type": physical[0], "e": {"k": "local", "name": "value"}}]
assert call["ret"] == call["dynRet"] == implementation["ret"]
assert all("_physicalDeclarationSignature" not in item for item in nodes(root))
print("shared source/native slot forwards to the exact selected implementation with its checked argument")
