#!/usr/bin/env python3
import base64
import json
import sys
from collections import Counter


def objects(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from objects(value)
    elif isinstance(node, list):
        for value in node:
            yield from objects(value)


if len(sys.argv) != 2:
    raise SystemExit(
        "usage: assert-referenced-generic-owner-unsafe-accessor-cir.py "
        "<ReferencedProtectedGenericOwnerTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

holders = [
    item
    for item in root.get("types", [])
    if item.get("generated") and item.get("name", "").startswith("dotkt$unsafe$holder$")
]
if len(holders) != 3:
    raise SystemExit(f"found {len(holders)} generic UnsafeAccessor holders, expected 3")

owner_tv = {"t": "tv", "scope": "type", "i": 0}
nullable_tv = {"t": "tv", "scope": "type", "i": 1}
base_open = {
    "t": "fqn",
    "name": "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase`2",
    "args": [owner_tv, nullable_tv],
}
physical_array = {"t": "fqn", "name": "System.Array"}
expected_params = [base_open, physical_array]
source_array = {"t": "array", "elem": {"t": "nullable", "of": owner_tv}}

accessor_targets = []
for holder in holders:
    if holder.get("typeParams") != [
        {"name": "__owner0", "constraints": [nullable_tv]}, {"name": "__owner1"}
    ]:
        raise SystemExit(f"UnsafeAccessor holder lost the referenced owner's generic frame: {holder!r}")
    accessors = [
        method
        for method in holder.get("methods", [])
        if method.get("generated") and method.get("static") and method.get("extern")
    ]
    if len(accessors) != 1:
        raise SystemExit(f"found {len(accessors)} holder UnsafeAccessor methods, expected 1")
    accessor = accessors[0]
    if [parameter.get("type") for parameter in accessor.get("params", [])] != expected_params:
        raise SystemExit(
            f"UnsafeAccessor does not state the referenced MethodDef's physical parameters: {accessor!r}"
        )
    if accessor.get("ret") != physical_array:
        raise SystemExit(f"UnsafeAccessor does not state the referenced MethodDef's physical return: {accessor!r}")
    wrappers = [method for method in holder.get("methods", []) if method.get("name", "").endswith("$invoke")]
    if (len(wrappers) != 1 or wrappers[0].get("ret") != physical_array
            or [param.get("type") for param in wrappers[0].get("params", [])] != expected_params):
        raise SystemExit(f"UnsafeAccessor wrapper does not preserve the complete owner frame: {wrappers!r}")
    for declaration in (accessor, wrappers[0]):
        carriers = [attribute for attribute in declaration.get("retAttrs", [])
                    if attribute.get("attr", {}).get("name")
                    == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"]
        if len(carriers) != 1:
            raise SystemExit("generic-owner UnsafeAccessor must retain one source array result carrier")
        arguments = carriers[0].get("args", [])
        if (len(arguments) != 2 or arguments[0].get("value") != "bir-json/1"
                or json.loads(base64.b64decode(arguments[1]["bytes"], validate=True)) != source_array):
            raise SystemExit("generic-owner UnsafeAccessor lost its source Array<T?> owner frame")
    names = [
        argument.get("value", {}).get("value")
        for attribute in accessor.get("attrs", [])
        for argument in attribute.get("namedArgs", [])
        if argument.get("name") == "Name"
    ]
    if len(names) != 1:
        raise SystemExit(f"UnsafeAccessor has no exact target name: {accessor!r}")
    accessor_targets.append(names[0])

if Counter(accessor_targets) != Counter({"snapshot": 2, "openSnapshot": 1}):
    raise SystemExit(f"final/open protected target set is incomplete: {accessor_targets!r}")

holder_names = {holder.get("name") for holder in holders}
wrapper_calls = [
    node
    for node in objects(root.get("types", []))
    if node.get("k") == "callStatic"
    and node.get("owner", {}).get("name") in holder_names
    and node.get("method", "").endswith("$invoke")
]
if len(wrapper_calls) != 3:
    raise SystemExit(f"found {len(wrapper_calls)} UnsafeAccessor wrapper calls, expected 3")

constructed_frames = Counter(
    tuple(argument.get("name") for argument in call.get("owner", {}).get("args", []))
    for call in wrapper_calls
)
if constructed_frames != Counter({("System.String", "System.String"): 2, ("System.Int32", "object"): 1}):
    raise SystemExit(f"holder calls use the wrong referenced owner frames: {constructed_frames!r}")

for call in wrapper_calls:
    physical_call_return = physical_array
    if call.get("sig") != expected_params or call.get("ret") != physical_call_return:
        raise SystemExit(f"holder call and physical accessor declaration disagree: {call!r}")

# A function value dispatches through the declared existential slot rather than
# creating a fourth native holder. Check that path instead of dropping coverage.
carrier_owner = {"t": "fqn", "name": "roundtrip.protectedgenericowner.ReferencedProtectedGenericOwnerBase$star"}
object_type = {"t": "fqn", "name": "System.Object"}
reference_calls = [node for node in objects(root.get("types", []))
                   if node.get("k") == "callInstance" and node.get("ownerType") == carrier_owner
                   and node.get("method") == "$star$snapshot$0"]
if len(reference_calls) != 1:
    raise SystemExit(f"expected one referenced snapshot function-value dispatch: {reference_calls!r}")
reference_call = reference_calls[0]
member = reference_call.get("memberRef", {})
if (reference_call.get("virtual") is not True or reference_call.get("sig") != [object_type]
        or reference_call.get("ret") != object_type or len(reference_call.get("args", [])) != 1
        or member.get("assembly") != "RoundtripProducer" or member.get("declaringType") != carrier_owner
        or member.get("name") != "$star$snapshot$0" or member.get("parameterTypes") != [object_type]
        or member.get("returnType") != object_type):
    raise SystemExit(f"snapshot function value does not bind the exact producer carrier slot: {reference_call!r}")
print(
    "referenced generic-owner direct/open/callable/value access keeps exact physical ABI "
    "and closes nullable companion results"
)
