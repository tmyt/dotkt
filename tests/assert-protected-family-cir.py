#!/usr/bin/env python3
"""Require the reviewed family-access fixtures to retain real direct edges and receiver frames."""
import json
import sys
from pathlib import Path

mode, directory = sys.argv[1:]

def walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)

def require(condition, message):
    if not condition:
        raise AssertionError(message)

filename = "ExplicitFieldSlotTests" if mode == "fields" else "ProtectedDefaultAccessTests" if mode == "native" else "ProtectedFamilyAccessTests" if mode != "producer" else "ProtectedFamilyAccess"
path = Path(directory) / (filename + ".cir.json")
require(path.is_file(), f"Missing actual emitted CIR: {path}")
document = json.loads(path.read_text())
types = {node["name"]: node for node in document.get("types", [])}

if mode == "fields":
    declaration = types["ProtectedFieldSlotSubclass"]
    expected = {"kind": "field", "assembly": "FieldSlotBase",
                "declaringType": {"t": "fqn", "name": "ExplicitFieldSlots.ProtectedFieldBase"},
                "name": "Value", "genericArity": 0,
                "returnType": {"t": "fqn", "name": "System.String"}}
    for method_name, kind in [("read", "field"), ("write", "setFieldExpr")]:
        method, = [method for method in declaration["methods"] if method["name"] == method_name]
        field, = [node for node in walk(method["body"]) if node.get("k") == kind and node.get("name") == "Value"]
        require(field.get("memberRef") == expected, "Protected field changed to a same-named interface slot")
        require(field.get("ownerType") == expected["declaringType"] and field.get("recv") == {"k": "this"},
                "Protected field lost its exact declaring owner/receiver")
        require(field.get("member") != "accessor" and "accessor" not in field, "Field reclassified as a property")
elif mode == "native":
    owner = "ProtectedFamilySignature.Base`1"
    token = {"t": "fqn", "name": owner + "+Token", "args": [{"t": "tv", "scope": "type", "i": 0}]}
    members = [node["memberRef"] for node in walk(document) if isinstance(node.get("memberRef"), dict)
               and node["memberRef"].get("declaringType", {}).get("name") == owner]
    require(any(member.get("name") == "MakeToken" and member.get("returnType") == token for member in members),
            "Native protected nested return no longer has its exact direct member")
    require(any(member.get("name") == "Describe" and member.get("parameterTypes") == [token] for member in members),
            "Native protected nested parameter no longer has its exact direct member")
    require(any(member.get("name") == "Saved" and member.get("returnType") == token for member in members),
            "Native protected nested field no longer has its exact direct member")
else:
    require(mode in ("basic", "producer", "consumer"), f"Unknown fixture mode: {mode}")
    derived = {"basic": "FamilyOuter.Derived", "producer": "roundtrip.protectedfamily.FamilyDerived",
               "consumer": "roundtriptests.protectedfamily.ImportedFamily"}[mode]
    base = "FamilyOuter.Base" if mode == "basic" else "roundtrip.protectedfamily.FamilyBase"
    selected_base = base + "`2" if mode == "consumer" else base
    require(derived in types, f"Missing actual derived declaration: {derived}")
    own_methods = types[derived].get("methods", [])
    reference_name = "inheritedBaseBound" if mode == "consumer" else "inheritedBound"
    references = [method for method in own_methods if method.get("name") == reference_name]
    require(len(references) == 1, "Missing actual inherited non-overridden callable reference")
    closures = [node for node in walk(references[0].get("body")) if node.get("k") == "newClosure"]
    require(len(closures) == 1, "Callable reference lost its actual closure representation")
    closure = types.get(closures[0].get("closureType", {}).get("name"))
    require(closure is not None, "Missing callable-reference closure declaration")
    require(any(node.get("k") == "callInstance" and node.get("ownerType", {}).get("name") == base + "$star"
                and node.get("method", "").startswith("$star$stable$") for node in walk(closure)),
            "Callable reference lost its selected inherited Base carrier slot")
    if mode != "consumer":
        calls = [node for method in own_methods if method.get("name") == "functions"
                 for node in walk(method.get("body")) if node.get("k") == "callInstance"]
        require({"transform", "prop_get<callback>"} <= {node.get("method") for node in calls
                if node.get("ownerType", {}).get("name") == base}, "Function-signature family calls are not direct")
        across = [method for method in own_methods if method.get("name") == "across"]
        require(len(across) == 1 and across[0].get("typeParams"), "Missing compiled different-construction source control")
        require(any(node.get("k") == "callInstance" and node.get("ownerType", {}).get("name") == base + "$star"
                    for node in walk(across[0].get("body"))), "Different-construction control lost its actual carrier path")

    def nested_in(name, parent):
        seen = set()
        while name in types and name not in seen:
            if name == parent:
                return True
            seen.add(name)
            name = types[name].get("nestedIn")
        return False

    checked = 0
    for name, declaration in types.items():
        if "dotkt$captured$" not in name or not nested_in(name, derived):
            continue
        for method in declaration.get("methods", []):
            for call in walk(method.get("body")):
                if call.get("k") != "callInstance" or call.get("method") != "takeBox":
                    continue
                receiver = method["params"][0]
                require(receiver["type"].get("name") == derived, f"Captured family receiver widened: {name}")
                require(call.get("ownerType", {}).get("name") == selected_base, "Captured call changed selected declaring owner")
                require(receiver["type"].get("args") == call["ownerType"].get("args"), "Captured receiver lost its complete constructed frame")
                require(call.get("recv") == {"k": "local", "name": receiver["name"]}, "Captured call lost its actual receiver slot")
                require(call.get("typeArgs") == [{"t": "tv", "scope": "method", "i": i}
                                                  for i in range(len(call.get("typeArgs", [])))],
                        "Captured call changed the selected method frame")
                require(call.get("typeArgs"), "Captured call did not actually close a generic method")
                if mode == "consumer":
                    member = call.get("memberRef", {})
                    require(member.get("declaringType") == call["ownerType"] and member.get("name") == "takeBox"
                            and member.get("genericArity") == len(call["typeArgs"])
                            and member.get("parameterTypes") == call.get("sig"), "Imported captured call changed its selected exact member")
                require(all(node.get("scope") == "method" and node["i"] < len(method["typeParams"])
                            for node in walk(receiver["type"]) if node.get("t") == "tv"), "Receiver left the thunk's physical generic frame")
                checked += 1
    require(checked >= (2 if mode == "consumer" else 3), f"Missing actual captured generic family calls: {checked}")
print(f"protected family CIR {mode}: direct members, inherited references and captured receiver frames OK")
