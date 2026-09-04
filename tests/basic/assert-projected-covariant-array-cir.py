#!/usr/bin/env python3
"""Assert exact CLR carriers for projected covariant arrays and exact invariant projections."""

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
    raise SystemExit("usage: assert-projected-covariant-array-cir.py <GenericsTests.cir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)


def method(name):
    matches = [item for item in root.get("methods", []) if item.get("name") == name]
    if len(matches) != 1:
        raise SystemExit(f"found {len(matches)} {name} methods, expected 1")
    return matches[0]


def array(element):
    return {"t": "array", "elem": element}


def fqn(name, *args):
    result = {"t": "fqn", "name": name}
    if args:
        result["args"] = list(args)
    return result


producer_carrier = fqn("Producer$star")
for method_name in ("initializedProjectedProducerArray", "charSequenceProducerArray"):
    declaration = method(method_name)
    if declaration.get("ret") != array(producer_carrier):
        raise SystemExit(f"{method_name} did not expose Producer$star[] physically: {declaration.get('ret')!r}")
    allocations = [
        node
        for node in objects(declaration.get("body", []))
        if node.get("k") in ("newArray", "newArrayInit")
    ]
    if len(allocations) != 1 or allocations[0].get("elem") != producer_carrier:
        raise SystemExit(f"{method_name} did not allocate Producer$star[]: {allocations!r}")

unsafe = method("unsafeVarianceProducerArray")
unsafe_carrier = fqn("UnsafeProducer$star")
if unsafe.get("ret") != array(unsafe_carrier):
    raise SystemExit(f"unsafe variance was emitted as CLR covariance: {unsafe.get('ret')!r}")

initialized = method("initializedProjectedProducerArray")
closures = [node for node in objects(initialized.get("body", [])) if node.get("k") == "newClosure"]
if len(closures) != 1 or closures[0].get("funcType", {}).get("ret") != producer_carrier:
    raise SystemExit(f"capturing Array(size) initializer did not adopt Producer$star: {closures!r}")

covariant_class = method("covariantClassArray")
covariant_carrier = fqn("CovariantValue$star")
if covariant_class.get("ret") != array(covariant_carrier):
    raise SystemExit(f"Kotlin class variance was mistaken for CLR class variance: {covariant_class.get('ret')!r}")

invariant = method("invariantProjectedValue")
exact_invariant = array(fqn("InvariantValue", fqn("System.String")))
parameters = invariant.get("params", [])
if len(parameters) != 1 or parameters[0].get("type") != exact_invariant:
    raise SystemExit(f"invariant projected element was over-erased: {parameters!r}")

print("projected covariant array carriers and invariant projection are exact")
