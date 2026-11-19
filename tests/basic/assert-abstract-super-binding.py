#!/usr/bin/env python3
"""Check bodyless abstract slots and direct, super and lifted accessor bindings."""

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


if len(sys.argv) != 3:
    raise SystemExit(
        "usage: assert-abstract-super-binding.py <RuntimeTypes...bir.json> <RuntimeTypes...cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    bir = json.load(stream)
with open(sys.argv[2], encoding="utf-8") as stream:
    cir = json.load(stream)


def type_decl(root, name):
    matches = [item for item in root.get("types", []) if item.get("name") == name]
    if len(matches) != 1:
        raise SystemExit(f"found {len(matches)} declarations for {name}, expected 1")
    return matches[0]


abstract = type_decl(bir, "RuntimeTypesAbstractBodylessContract")
abstract_methods = [method for method in abstract.get("methods", []) if method.get("abstract") is True]
if not abstract_methods:
    raise SystemExit("abstract bodyless fixture emitted no abstract methods")
for method in abstract_methods:
    if method.get("body") != []:
        raise SystemExit(
            f"abstract method {method.get('name')} carries executable BIR: {method.get('body')!r}"
        )

expected = {
    "inheritedMethod": ("RuntimeTypesSuperMethodMiddle", "RuntimeTypesSuperMethodBase"),
    "inheritedProperty": ("RuntimeTypesSuperPropertyMiddle", None),
}
derived_names = {
    "RuntimeTypesSuperMethodDerived",
    "RuntimeTypesSuperPropertyDerived",
}
bir_calls = []
for name in derived_names:
    declaration = type_decl(bir, name)
    bir_calls.extend(
        node for node in objects(declaration.get("methods", []))
        if node.get("k") == "callInstance" and node.get("super") is True
    )

for method, (owner, _) in expected.items():
    matches = [call for call in bir_calls if call.get("method") == method]
    if len(matches) != 1:
        raise SystemExit(f"found {len(matches)} BIR super calls for {method}, expected 1: {bir_calls!r}")
    actual_owner = matches[0].get("ownerType", {}).get("name")
    if actual_owner != owner or matches[0].get("virtual") is not False:
        raise SystemExit(
            f"BIR {method} must preserve the immediate Kotlin owner {owner}, got {matches[0]!r}"
        )

method_bir = next(call for call in bir_calls if call.get("method") == "inheritedMethod")
if method_bir.get("sig") != [{"t": "fqn", "name": "kotlin.String"}]:
    raise SystemExit(f"BIR method overload signature is not exact: {method_bir!r}")

cir_calls = []
for name in derived_names:
    declaration = type_decl(cir, name)
    cir_calls.extend(
        node for node in objects(declaration.get("methods", []))
        if node.get("k") == "callInstance" and node.get("virtual") is False
    )

cir_expected = {
    "inheritedMethod": "RuntimeTypesSuperMethodBase",
    "prop_get<inheritedProperty>": "RuntimeTypesSuperPropertyBase",
}
for method, owner in cir_expected.items():
    matches = [call for call in cir_calls if call.get("method") == method]
    if len(matches) != 1:
        raise SystemExit(f"found {len(matches)} CIR super calls for {method}, expected 1: {cir_calls!r}")
    actual_owner = matches[0].get("ownerType", {}).get("name")
    if actual_owner != owner or matches[0].get("virtual") is not False:
        raise SystemExit(f"CIR {method} must be a non-virtual call to {owner}, got {matches[0]!r}")

# Bound and unbound property references must carry the same selected accessor and
# instantiated override edges as the direct calls, even when all three dispatch correctly.
def slot_calls(declaration):
    bindings = {}
    for node in objects(declaration["methods"]):
        if (node.get("k") == "callInstance" and node.get("method") == "slotValue"
                and node["ownerType"].get("name") == "RuntimeTypesStringSlot"):
            binding = (node["declarationId"], node.get("overrides"))
            assert bindings.setdefault(node["prop"], binding) == binding, "get and invoke disagree"
    return bindings

direct = slot_calls(type_decl(bir, "RuntimeTypeAndSuperDispatchTests"))
assert set(direct) == {"get", "set"}
for _, edges in direct.values():
    assert len(edges) == 1 and edges[0]["owner"] == {
        "t": "fqn", "name": "RuntimeTypesSlot", "args": [{"t": "fqn", "name": "kotlin.String"}]}
for arity in (0, 1):
    protocol = f"kotlin.reflect.KMutableProperty{arity}"
    matches = [calls for decl in bir["types"]
               if any(i.get("name") == protocol for i in decl.get("interfaces", []))
               and (calls := slot_calls(decl))]
    assert matches == [direct], f"{protocol}: selected accessor or override edge differs from direct access"

print("abstract slots, class-super calls and lifted accessor bindings OK")
