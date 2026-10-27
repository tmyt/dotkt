#!/usr/bin/env python3
"""Native location/proof roles remain exact without changing ordinary Kotlin storage."""
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


def facts(declaration, attribute):
    return [json.loads(base64.b64decode(item["args"][1]["bytes"]))
            for item in declaration.get("attrs", [])
            if item.get("attr", {}).get("name")
            == "DotKt.Runtime.CompilerServices." + attribute + "Attribute"]


producer, native, dependent = [json.load(open(path, encoding="utf-8")) for path in sys.argv[1:]]
cell = "roundtrip.constructorcarrier.ExportedNativeCell"
definition = next(item for item in producer["types"] if item["name"] == cell)
field = next(item for item in definition["fields"] if item["name"] == "value")
assert field["type"] == {"t": "tv", "scope": "type", "i": 0}
carrier = next(item for item in producer["types"] if item["name"] == cell + "$star")
getter = next(item for item in carrier["methods"]
              if {"name": "value", "kind": "get", "association": cell + ":field:value"}
              in facts(item, "KotlinPropertyAccessor"))
replace = next(item for item in native["methods"] if item["name"] == "replaceExportedField")
assert replace["params"][0]["type"] == {"t": "fqn", "name": carrier["name"]}
call = next(item for item in nodes(replace["body"]) if item.get("k") == "clrGenericStatic")
tv = {"t": "tv", "scope": "method", "i": 0}
assert call["typeArgs"] == [tv]
assert call["memberRef"]["parameterTypes"][:2] == [{"t": "byRef", "of": tv}] * 2
addresses = call["args"][:2]
assert addresses[0] == addresses[1], "both refs must address the same original field, not distinct spills"
address = addresses[0]
assert address["k"] == "field" and address["name"] == field["name"]
assert address["ownerType"] == address["memberRef"]["declaringType"] == {
    "t": "fqn", "name": cell + "`2", "args": [tv, {"t": "tv", "scope": "method", "i": 1}]}
assert address["memberRef"]["kind"] == "field" and address["memberRef"]["returnType"] == field["type"]
assert address["ret"] == tv
assert address["recv"] == {"k": "cast", "type": address["ownerType"], "e": {"k": "local", "name": "cell"}}
ordinary = next(item for item in nodes(native)
                if item.get("name") == "importedNativeFieldDoesNotUseItsPrivateStorageCompanion" and "body" in item)
assert any(item.get("k") == "callInstance" and item.get("method") == getter["name"]
           and item.get("ownerType", {}).get("name") == carrier["name"] for item in nodes(ordinary["body"]))

bounded = next(item for item in native["types"] if item["name"].endswith(".StoredNativeBoundedValueEcho"))
entry = next(item for item in bounded["methods"] if item["name"] == "Echo")
exact = {"t": "fqn", "name": "roundtrip.constructorcarrier.BoundCounter`1",
         "args": [{"t": "fqn", "name": "System.Int32"}]}
assert entry["typeParams"][0]["constraints"] == [exact]
helpers = [item for item in bounded["methods"] if item["vis"] == "private"]
assert len(helpers) == 1 and len(helpers[0]["typeParams"]) == 2
assert helpers[0]["typeParams"][0]["constraints"] == [
    {"t": "fqn", "name": "roundtrip.constructorcarrier.BoundCounter$star"}]
assert "constraints" not in helpers[0]["typeParams"][1]

for suffix in (".GenericStoredNativeBoundedValueEcho", ".GenericStoredNativeBoundedValueEcho$star"):
    generic = next(item for item in native["types"] if item["name"].endswith(suffix))
    public = next(item for item in generic["methods"] if item["name"] == "Echo")
    assert public["typeParams"][0]["constraints"] == [exact]
    bodies = [item for item in generic["methods"] if "$nullableBody$" in item["name"]]
    assert len(bodies) == (1 if suffix.endswith("$star") else 2)
    for body in bodies:
        assert body["typeParams"][0]["constraints"] == [
            {"t": "fqn", "name": "roundtrip.constructorcarrier.BoundCounter$star"}]
        assert "constraints" not in body["typeParams"][1]

proof = {"t": "fqn", "name": "roundtrip.dependentnative.ForeignBoundBase`1",
         "args": [{"t": "fqn", "name": "System.String"}]}
bridge_owner = next(item for item in native["types"] if item["name"].endswith(".GenericNativeProofBridge"))
bridge_carrier = next(item for item in native["types"] if item["name"] == bridge_owner["name"] + "$star")
slot = next(item for item in bridge_carrier["methods"]
            if any(fact.get("name") == "checked" and fact.get("sourceDeclaration")
                   for fact in facts(item, "KotlinSourceMethod")))
forwarder = next(item for item in bridge_owner["methods"] if item.get("clrInterfaceImpls")
                 and any(edge.get("owner", {}).get("name") == bridge_carrier["name"]
                         and edge.get("member") == slot["name"] for edge in item["clrInterfaceImpls"]))
for declaration in (slot, forwarder, *forwarder["clrInterfaceImpls"]):
    assert proof in declaration["typeParams"][0]["constraints"]

bound_name = "roundtrip.dependentnative.ForeignBoundBase"
source_bound = {"t": "fqn", "name": bound_name + "$star"}
for name in ("KotlinBoundOwner", "KotlinMethodBoundOwner"):
    owner = next(item for item in dependent["types"] if item["name"] == name)
    constraints = owner["typeParams"][1]["constraints"]
    assert source_bound in constraints
    assert {"t": "fqn", "name": bound_name + "`1", "args": [
        {"t": "tv", "scope": "type", "i": 0}]} in constraints
    assert len(constraints) == len({json.dumps(row, sort_keys=True) for row in constraints})
method = next(item for item in dependent["methods"] if item["name"] == "callForeignKotlinBound")
assert {"t": "fqn", "name": bound_name + "`1", "args": [
    {"t": "tv", "scope": "method", "i": 0}]} in method["typeParams"][1]["constraints"]
pure = next(item for item in dependent["methods"] if item["name"] == "sourceBoundOnly")
assert pure["typeParams"][1]["constraints"] == [source_bound]
print("native field addresses and demanded physical proofs stay exact; ordinary storage stays projected")
