#!/usr/bin/env python3
"""Assert star-copy defaults and owner-dependent results stay on exact existential carriers."""

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
    raise SystemExit(
        "usage: assert-star-copy-default-fields-cir.py <DefaultArgumentTests.cir.json>"
    )

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

for semantic_fact in ("dataClassCopyDefault", "generatedEqualsFieldRead"):
    if any(node.get(semantic_fact) is not None for node in objects(root)):
        raise SystemExit(f"CIR: unconsumed {semantic_fact} semantic fact")

if any(node.get("_existentialResultProjection") is not None for node in objects(root)):
    raise SystemExit("CIR: unconsumed existential-result projection fact")

for helper, star_owner in (
    ("defaultArgCopyThroughStar", "DefaultArgStarCopy$star"),
    ("defaultArgOverrideCopyThroughStar", "DefaultArgStarOverrideCopy$star"),
):
    methods = [
        method
        for method in root.get("methods", [])
        if isinstance(method, dict) and method.get("name") == helper
    ]
    if len(methods) != 1:
        raise SystemExit(f"CIR: found {len(methods)} {helper} helpers, expected 1")

    body = methods[0].get("body", [])
    star_fields = [
        node
        for node in objects(body)
        if node.get("k") == "field"
        and isinstance(node.get("ownerType"), dict)
        and node["ownerType"].get("name") == star_owner
    ]
    if star_fields:
        raise SystemExit(
            "CIR: fieldless existential owner still receives direct field reads: "
            f"{[node.get('name') for node in star_fields]!r}"
        )

    default_reads = [
        node
        for node in objects(body)
        if node.get("k") == "callInstance"
        and isinstance(node.get("ownerType"), dict)
        and node["ownerType"].get("name") == star_owner
        and isinstance(node.get("recv"), dict)
        and node["recv"].get("k") == "local"
        and node["recv"].get("name") == "source"
        and node.get("sig") == []
    ]
    returns = sorted(
        node.get("ret", {}).get("name")
        for node in default_reads
        if isinstance(node.get("ret"), dict)
    )
    if returns != ["System.Object", "System.String"] or not all(
        node.get("virtual") is True for node in default_reads
    ):
        raise SystemExit(
            "CIR: copy defaults must be two virtual existential getter calls returning object/string: "
            f"{default_reads!r}"
        )

for class_name, star_owner in (
    ("DefaultArgStarCopy", "DefaultArgStarCopy$star"),
    ("DefaultArgStarOverrideCopy", "DefaultArgStarOverrideCopy$star"),
):
    declarations = [
        declaration
        for declaration in root.get("types", [])
        if isinstance(declaration, dict) and declaration.get("name") == class_name
    ]
    methods = [
        method
        for declaration in declarations
        for method in declaration.get("methods", [])
        if isinstance(method, dict) and method.get("name") == "Equals"
    ]
    if len(methods) != 1:
        raise SystemExit(f"CIR: found {len(methods)} {class_name}.Equals methods, expected 1")
    peer_reads = [
        node
        for node in objects(methods[0].get("body", []))
        if node.get("k") == "callInstance"
        and isinstance(node.get("ownerType"), dict)
        and node["ownerType"].get("name") == star_owner
        and isinstance(node.get("recv"), dict)
        and node["recv"].get("k") == "local"
    ]
    returns = sorted(
        node.get("ret", {}).get("name")
        for node in peer_reads
        if isinstance(node.get("ret"), dict)
    )
    if returns != ["System.Object", "System.String"] or not all(
        node.get("virtual") is True for node in peer_reads
    ):
        raise SystemExit(
            f"CIR: {class_name}.Equals peer fields must use two physical existential getter slots: "
            f"{peer_reads!r}"
        )

nested_helpers = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict) and method.get("name") == "defaultArgNestedCopyThroughStar"
]
if len(nested_helpers) != 1:
    raise SystemExit(
        f"CIR: found {len(nested_helpers)} defaultArgNestedCopyThroughStar helpers, expected 1"
    )

nested_body = nested_helpers[0].get("body", [])
forbidden_concrete_casts = [
    node
    for node in objects(nested_body)
    if node.get("k") == "cast"
    and isinstance(node.get("type"), dict)
    and node["type"].get("name") == "DefaultArgStarNested"
    and node["type"].get("args") is not None
]
if forbidden_concrete_casts:
    raise SystemExit(
        "CIR: star-dependent nested results must not cast to an invariant concrete construction: "
        f"{forbidden_concrete_casts!r}"
    )

nested_carrier = {"t": "fqn", "name": "DefaultArgStarNested$star"}
nested_getters = [
    node
    for node in objects(nested_body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "DefaultArgStarNestedCopy$star"
    and node.get("ret") == nested_carrier
    and node.get("sig") == []
]
if len(nested_getters) != 2 or not all(
    node.get("virtual") is True for node in nested_getters
):
    raise SystemExit(
        "CIR: copy default and ordinary nested getter must both return the exact nested carrier: "
        f"{nested_getters!r}"
    )

nested_locals = [
    node
    for node in nested_body
    if isinstance(node, dict)
    and node.get("k") == "var"
    and node.get("type") == nested_carrier
    and isinstance(node.get("init"), dict)
    and node["init"].get("k") == "cast"
    and node["init"].get("type") == nested_carrier
]
if len(nested_locals) != 2:
    raise SystemExit(
        f"CIR: nested and chained locals must retain the physical existential carrier: {nested_locals!r}"
    )

again_calls = [
    node
    for node in objects(nested_body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "DefaultArgStarNested$star"
    and str(node.get("method", "")).startswith("$star$again$")
    and node.get("ret") == nested_carrier
]
if len(again_calls) != 1:
    raise SystemExit(
        f"CIR: chained owner-dependent result must stay on the nested carrier: {again_calls!r}"
    )

value_getters = [
    node
    for node in objects(nested_body)
    if node.get("k") == "callInstance"
    and node.get("ownerType", {}).get("name") == "DefaultArgStarNested$star"
    and node.get("ret", {}).get("name") == "System.Object"
]
if len(value_getters) != 1 or value_getters[0].get("virtual") is not True:
    raise SystemExit(
        "CIR: use after the nested local must bind through its existential value getter: "
        f"{value_getters!r}"
    )

inherited_helpers = [
    method
    for method in root.get("methods", [])
    if isinstance(method, dict)
    and method.get("name") == "defaultArgInheritedNestedThroughReorderedFrame"
]
if len(inherited_helpers) != 1:
    raise SystemExit(
        "CIR: expected one reordered inherited-frame helper, found "
        f"{len(inherited_helpers)}"
    )
inherited_locals = [
    node
    for node in inherited_helpers[0].get("body", [])
    if isinstance(node, dict)
    and node.get("k") == "var"
    and node.get("type") == nested_carrier
    and isinstance(node.get("init"), dict)
    and node["init"].get("k") == "cast"
    and node["init"].get("type") == nested_carrier
    and node["init"].get("e", {}).get("ownerType", {}).get("name")
    == "DefaultArgStarBase$star"
    and node["init"].get("e", {}).get("ret") == nested_carrier
]
if len(inherited_locals) != 1:
    raise SystemExit(
        "CIR: the inherited nested result must use its ordinary value carrier: "
        f"{inherited_locals!r}"
    )
derived = next(item for item in root["types"] if item.get("name") == "DefaultArgStarDerived")
if derived.get("base") != {"t": "fqn", "name": "DefaultArgStarBase", "args": [
        {"t": "tv", "scope": "type", "i": 1}, {"t": "tv", "scope": "type", "i": 2}]}:
    raise SystemExit(f"CIR: inherited construction lost B's ordinary/storage frames: {derived.get('base')!r}")
logical_parameters = [json.loads(base64.b64decode(attribute["args"][1]["bytes"]))
                      for attribute in inherited_helpers[0]["params"][0].get("attrs", [])
                      if attribute.get("attr", {}).get("name")
                      == "DotKt.Runtime.CompilerServices.KotlinTypeAttribute"]
if logical_parameters != [{"t": "fqn", "name": "DefaultArgStarDerived", "args": [
        {"t": "star"}, {"t": "fqn", "name": "kotlin.String"}]}]:
    raise SystemExit(f"CIR: reordered inherited source frame was lost: {logical_parameters!r}")
typed_reads = [node for node in objects(inherited_helpers[0]["body"])
               if node.get("k") == "cast" and node.get("type") == {"t": "fqn", "name": "System.String"}
               and node.get("e", {}).get("ownerType") == nested_carrier
               and node["e"].get("ret") == {"t": "fqn", "name": "System.Object"}]
if len(typed_reads) != 1:
    raise SystemExit(f"CIR: Base<B> did not retain the inherited result's String use: {typed_reads!r}")

print("star-projected data-class reads and nested results use exact existential carriers")
