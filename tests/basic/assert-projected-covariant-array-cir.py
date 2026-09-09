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
for method_name in (
    "exactCovariantProducerArray",
    "initializedProjectedProducerArray",
    "charSequenceProducerArray",
):
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

writable_parameter = method("writeProjectedProducerParameter")
if writable_parameter.get("params", [{}])[0].get("type") != array(producer_carrier):
    raise SystemExit(
        f"writable projected parameter did not expose Producer$star[]: {writable_parameter.get('params')!r}"
    )
parameter_writes = [
    node for node in objects(writable_parameter.get("body", [])) if node.get("k") == "arraySet"
]
if len(parameter_writes) != 2 or any(node.get("elem") != producer_carrier for node in parameter_writes):
    raise SystemExit(f"writable projected parameter stores were not Producer$star: {parameter_writes!r}")

writable_storage = method("mutableProjectedProducerStorage")
sized_allocations = [
    node for node in objects(writable_storage.get("body", [])) if node.get("k") == "newArraySized"
]
if len(sized_allocations) != 3 or any(node.get("elem") != producer_carrier for node in sized_allocations):
    raise SystemExit(f"sized projected arrays did not allocate Producer$star[]: {sized_allocations!r}")
writable_ops = [
    node
    for node in objects(writable_storage.get("body", []))
    if node.get("k") in ("arrayGet", "arraySet", "forArray")
]
if not writable_ops or any(node.get("elem") != producer_carrier for node in writable_ops):
    raise SystemExit(f"projected array storage operations did not use Producer$star: {writable_ops!r}")
generic_reads = [
    node
    for node in objects(writable_storage.get("body", []))
    if node.get("method") in ("firstProjectedValue", "first") and node.get("typeArgs") is not None
]
if len(generic_reads) != 2 or any(
    node.get("typeArgs") != [producer_carrier] or node.get("ret") != producer_carrier
    for node in generic_reads
):
    raise SystemExit(f"generic projected-array reads did not close over Producer$star: {generic_reads!r}")

holders = [item for item in root.get("types", []) if item.get("name") == "ProjectedProducerArrayHolder"]
if len(holders) != 1:
    raise SystemExit(f"found {len(holders)} projected array holders, expected 1")
holder = holders[0]
holder_slots = [field.get("type") for field in holder.get("fields", [])]
holder_slots += [prop.get("type") for prop in holder.get("properties", [])]
holder_slots += [param.get("type") for ctor in holder.get("ctors", []) for param in ctor.get("params", [])]
if not holder_slots or any(slot != array(producer_carrier) for slot in holder_slots):
    raise SystemExit(f"projected array holder slots did not use Producer$star[]: {holder_slots!r}")

consumer_carrier = fqn("Consumer$star")
contravariant_storage = method("mutableContravariantConsumerStorage")
contravariant_ops = [
    node
    for node in objects(contravariant_storage.get("body", []))
    if node.get("k") in ("newArraySized", "arrayGet", "arraySet")
]
if not contravariant_ops or any(node.get("elem") != consumer_carrier for node in contravariant_ops):
    raise SystemExit(f"contravariant array storage operations did not use Consumer$star: {contravariant_ops!r}")

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

# Declaration-site variance on a Kotlin class is source-level substitutability, not CLR class variance. Every
# widened value slot therefore uses the declaration's existential interface, while allocations retain the exact
# constructed class whose constructor is being invoked.
widening = method("covariantClassWidening")
widening_nodes = list(objects(widening.get("body", [])))
expected_carriers = {
    "CovariantValue$star",
    "PrivateCovariantValue$star",
    "ContravariantAction$star",
}
observed_carriers = {
    node.get("type", {}).get("name")
    for node in widening_nodes
    if node.get("k") == "var" and node.get("type", {}).get("name") in expected_carriers
}
if observed_carriers != expected_carriers:
    raise SystemExit(f"variant class values did not use their existential carriers: {observed_carriers!r}")

expected_constructions = {
    ("CovariantValue", "System.Int32"),
    ("CovariantValue", "System.String"),
    ("PrivateCovariantValue", "System.Int32"),
    ("ContravariantAction", "System.Object"),
}
observed_constructions = {
    (node.get("type", {}).get("name"), node.get("type", {}).get("args", [{}])[0].get("name"))
    for node in widening_nodes
    if node.get("k") == "new"
    and node.get("type", {}).get("name") in {
        "CovariantValue",
        "PrivateCovariantValue",
        "ContravariantAction",
    }
}
if observed_constructions != expected_constructions:
    raise SystemExit(f"variant class constructors lost their exact constructed heads: {observed_constructions!r}")

carrier_calls = [
    node
    for node in widening_nodes
    if node.get("k") == "callInstance" and node.get("ownerType", {}).get("name") in expected_carriers
]
if not carrier_calls or any(node.get("ownerType", {}).get("args") for node in carrier_calls):
    raise SystemExit(f"variant class members did not bind through non-generic carriers: {carrier_calls!r}")

holder_types = [item for item in root.get("types", []) if item.get("name") == "CovariantValueHolder"]
if len(holder_types) != 1:
    raise SystemExit(f"found {len(holder_types)} CovariantValueHolder declarations, expected 1")
holder_type = holder_types[0]
holder_value_slots = [field.get("type") for field in holder_type.get("fields", [])]
holder_value_slots += [prop.get("type") for prop in holder_type.get("properties", [])]
holder_value_slots += [param.get("type") for ctor in holder_type.get("ctors", []) for param in ctor.get("params", [])]
if not holder_value_slots or any(slot != covariant_carrier for slot in holder_value_slots):
    raise SystemExit(f"variant class declaration slots did not use CovariantValue$star: {holder_value_slots!r}")

user_named_lexical = method("userNamedLexicalVariance")
user_named_parameter_types = [parameter.get("type") for parameter in user_named_lexical.get("params", [])]
if user_named_parameter_types != [covariant_carrier]:
    raise SystemExit(
        "user declarations named like generated lexical receivers were incorrectly kept exact: "
        f"{user_named_parameter_types!r}"
    )

invariant = method("invariantProjectedValue")
exact_invariant = array(fqn("InvariantValue", fqn("System.String")))
parameters = invariant.get("params", [])
if len(parameters) != 1 or parameters[0].get("type") != exact_invariant:
    raise SystemExit(f"invariant projected element was over-erased: {parameters!r}")

print("projected covariant array carriers and invariant projection are exact")
