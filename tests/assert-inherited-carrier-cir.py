#!/usr/bin/env python3
"""Pin exact inherited interface contracts, independently of permissive runtime upcasts."""
import json
import sys


def fqn(name, *args):
    result = {"t": "fqn", "name": name}
    if args:
        result["args"] = [fqn(arg) for arg in args]
    return result


def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)


lane, path = sys.argv[1:]
with open(path, encoding="utf-8") as stream:
    types = {item["name"]: item for item in json.load(stream)["types"]}
if lane == "basic":
    named = fqn("InheritedCarrierName", "System.String")
    base = fqn("InheritedCarrierGenericBase$star")
    required = {
        "InheritedCarrierTask$star": [fqn("InheritedCarrierRunnable"), named],
        "InheritedCarrierDirect$star": [base, named],
        "InheritedCarrierIndirect$star": [base, named],
    }
elif lane == "consumer":
    named = fqn("inheritedcarrier.NameContract`1", "System.String")
    base = fqn("inheritedcarrier.GenericBase$star")
    required = {
        "ReferencedCarrierTask$star": [fqn("inheritedcarrier.RunnableContract"), named],
        "ReferencedCarrierRedeclared$star": [named],
        "ReferencedCarrierDirect$star": [base, named],
        "ReferencedCarrierIndirect$star": [base, named],
        "ReferencedCarrierUnit$star": [fqn("inheritedcarrier.UnitContract`1", "kotlin.Unit")],
    }
elif lane == "interop":
    required = {"ClrInheritedCarrierTask$star": [
        fqn("InheritedCarrierInterop.IValue`1", "System.String"), fqn("InheritedCarrierInterop.IStamp")
    ]}
else:
    raise AssertionError(f"unknown fixture lane: {lane}")

for name, contracts in required.items():
    owner = types[name]
    assert owner["kind"] == "interface" and owner.get("base") is None, name
    interfaces = owner["interfaces"]
    keys = [json.dumps(item, sort_keys=True) for item in interfaces]
    assert len(keys) == len(set(keys)), (name, "duplicate InterfaceImpl", interfaces)
    for contract in contracts:
        assert interfaces.count(contract) == 1, (name, contract, interfaces)
    assert not any(node.get("t") in ("tv", "star") or node.get("name") in ("void", "System.Void")
                   for node in nodes(interfaces)), (name, interfaces)
print(f"{lane}: inherited carrier contracts are exact and unique")
