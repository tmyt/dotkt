#!/usr/bin/env python3
"""Caller-frame results and source collection classifiers survive the ordinary-value carrier projection."""
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


def facts(declaration, name):
    return [json.loads(base64.b64decode(attr["args"][1]["bytes"])) for attr in declaration.get("attrs", [])
            if attr.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices." + name + "Attribute"]


producer, consumer, iterable_producer, iterable = [json.load(open(path, encoding="utf-8")) for path in sys.argv[1:]]
base_name = "roundtrip.innerresults.ResultOuter.Base"
base = next(item for item in producer["types"] if item["name"] == base_name)
carrier = next(item for item in producer["types"]
               if any(fact.get("name") == base_name for fact in facts(item, "KotlinType")))
cell = next(item for item in producer["types"]
            if any(fact.get("name") == "roundtrip.innerresults.ResultCell" for fact in facts(item, "KotlinType")))
cell_getter = next(method for method in cell["methods"]
                   if any(fact.get("name") == "value" and fact.get("kind") == "get"
                          for fact in facts(method, "KotlinPropertyAccessor")))
selected = {}
for name in ("readOwn", "readValue", "ownCell", "valueCell"):
    original = next(method for method in base["methods"] if method["name"] == name)
    identity = facts(original, "KotlinDeclarationIdentity")[0]["id"]
    slot = next(method for method in carrier["methods"]
                if {"name": name, "sourceDeclaration": identity} in facts(method, "KotlinSourceMethod"))
    selected[name] = slot["name"]
for name in ("own", "value"):
    slot = next(method for method in carrier["methods"]
                if any(fact.get("name") == name and fact.get("kind") == "get"
                       for fact in facts(method, "KotlinPropertyAccessor")))
    selected[name] = slot["name"]


def tv(index):
    return {"t": "tv", "scope": "type", "i": index}


for root, owner_name in ((producer, "roundtrip.innerresults.ResultHost"),
                         (consumer, "roundtriptests.innerresults.ImportedResultHost")):
    owner = next(item for item in root["types"] if item["name"] == owner_name)
    assert len(owner["typeParams"]) == 8
    method = next(item for item in owner["methods"] if item["name"] == "check")
    casts = [item for item in nodes(method["body"]) if item.get("k") == "cast"]
    for name, index in (("own", 4), ("readOwn", 4), ("value", 5), ("readValue", 5)):
        conversions = [item for item in casts if item.get("e", {}).get("k") == "callInstance"
                       and item["e"].get("method") == selected[name]]
        assert len(conversions) == 1 and conversions[0]["type"] == tv(index)
    for name, index in (("ownCell", 6), ("valueCell", 7)):
        conversions = [item for item in casts if item.get("e", {}).get("k") == "callInstance"
                       and any(call.get("k") == "callInstance" and call.get("method") == selected[name]
                               for call in nodes(item["e"].get("recv")))]
        assert len(conversions) == 1 and conversions[0]["type"] == tv(index)
        getter = conversions[0]["e"]
        assert getter["ownerType"] == {"t": "fqn", "name": cell["name"]}
        assert getter["method"] == cell_getter["name"] and getter["ret"] == cell_getter["ret"]
        assert getter["sig"] == [param["type"] for param in cell_getter["params"]] == []
        if root is consumer:
            assert getter["memberRef"]["declaringType"] == getter["ownerType"]
            assert getter["memberRef"]["parameterTypes"] == getter["sig"]
    child_name = "roundtrip.innerresults.GenericResult.Child" if root is producer \
        else "roundtrip.innerresults.GenericResult`5+Child`3"
    construction, = [item for item in nodes(method["body"]) if item.get("k") == "new"
                     and item.get("type", {}).get("name") == child_name]
    assert construction["type"]["args"] == [tv(index) for index in (0, 1, 2, 3, 7, 4, 5, 6)]
    if root is consumer:
        assert construction["memberRef"]["declaringType"] == construction["type"]

readonly = "roundtriptests.iterableidentity.ImportedIterableChild"
child = next(item for item in iterable["types"] if item["name"] == readonly)
view = next(item for item in iterable["types"]
            if any(fact.get("name") == readonly for fact in facts(item, "KotlinType")))
assert child["base"]["name"] == "roundtrip.iterableidentity.ReadOnlyIterable`2"
assert any(edge["name"] == "roundtrip.iterableidentity.ReadOnlyIterable$star" for edge in view["interfaces"])
assert not any(edge["name"] == "DotKt.Runtime.CompilerServices.KotlinMutableIterableClassifier"
               for declaration in (child, view) for edge in declaration.get("interfaces", []))
inline = next(method for method in iterable_producer["methods"] if any(
    fact.get("name") == "inlineReadOnlyChild" for fact in facts(method, "KotlinDeclarationIdentity")))
payload, = facts(inline, "KotlinInline")
assert payload["params"][0]["type"]["t"] == "fn"
selected_new, = [item for item in nodes(payload["body"]) if item.get("k") == "new"]
selected_identity = selected_new["constructorDeclarationId"]
declaration = next(item for item in iterable_producer["types"] if any(
    fact.get("id") == selected_identity for ctor in item.get("ctors", [])
    for fact in facts(ctor, "KotlinDeclarationIdentity")))
assert declaration["base"]["name"] == "roundtrip.iterableidentity.ReadOnlyIterable"
assert {"t": "fqn", "name": "DotKt.Runtime.CompilerServices.KotlinIterableClassifier"} in declaration["interfaces"]
assert not any(edge["name"] == "DotKt.Runtime.CompilerServices.KotlinMutableIterableClassifier"
               for edge in declaration["interfaces"])
# Anonymous object synthesis is producer-owned: splicing constructs that selected class directly, not a fresh
# consumer TypeDef. Prove actual expansion and exact construction without depending on its allocated name/ordinal.
assert not any(item.get("k") == "callStatic" and item.get("method") == inline["name"]
               and item.get("owner", {}).get("name") == payload["owner"] for item in nodes(iterable))
spliced, = [item for item in nodes(iterable) if item.get("k") == "new"
            and item.get("type", {}).get("name") == selected_new["type"]["name"]]
assert spliced["type"]["args"] == [{"t": "fqn", "name": "System.String"}] * 2
assert spliced["memberRef"]["kind"] == "ctor" and spliced["memberRef"]["declaringType"] == spliced["type"]
print("direct and chained results keep caller storage frames; imported/inline readonly classifiers stay readonly")
