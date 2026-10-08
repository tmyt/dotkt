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


def require(condition, message):
    if not condition:
        raise ValueError(message)


def one(items, description):
    require(len(items) == 1, f"expected one {description}, found {len(items)}")
    return items[0]


def fqn(name, args=None):
    result = {"t": "fqn", "name": name}
    if args is not None:
        result["args"] = args
    return result


def target_name(method):
    names = [argument.get("value", {}).get("value")
             for attribute in method.get("attrs", [])
             if attribute.get("attr", {}).get("name")
             == "System.Runtime.CompilerServices.UnsafeAccessorAttribute"
             for argument in attribute.get("namedArgs", [])
             if argument.get("name") == "Name"]
    return names[0] if len(names) == 1 else None


def source_type(attributes, expected):
    carrier = one([attribute for attribute in attributes
                   if attribute.get("attr", {}).get("name")
                   == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute"],
                  "Kotlin nullable-generic source carrier")
    args = carrier.get("args", [])
    require(len(args) == 2 and args[0].get("value") == "bir-json/1",
            "source carrier lost its codec")
    require(json.loads(base64.b64decode(args[1]["bytes"], validate=True)) == expected,
            "source carrier lost the base owner's nullable type parameter")


def validate(root):
    types = root.get("types", [])
    object_type = fqn("object")
    int_type = fqn("System.Int32")
    nullable_int = {"t": "nullable", "of": int_type}
    type_t = {"t": "tv", "scope": "type", "i": 0}
    nullable_t = {"t": "nullable", "of": type_t}

    def owner(name):
        return one([item for item in types if item.get("name") == name], name)

    def method(declaration, name):
        return one([item for item in declaration.get("methods", [])
                    if item.get("name") == name], f"{declaration['name']}.{name}")

    # Nullable generic references dispatch through the declared carrier slot;
    # the carrier forwards to the original open base MethodDef, not a derived
    # specialization. Concrete nullable projections belong to the consumer.
    for base, derived, name in (
        ("ProtectedNullableCallable", "ProtectedNullableIntCallable", "echo"),
        ("PublicNullableCallable", "PublicNullableIntCallable", "echoPublic"),
    ):
        declaration = owner(base)
        require(declaration.get("typeParams") == ["T"], "base lost its generic frame")
        original = method(declaration, name)
        require([p.get("type") for p in original.get("params", [])] == [object_type]
                and original.get("ret") == object_type, "base MethodDef lost its object ABI")
        source_type(original["params"][0].get("attrs", []), nullable_t)
        source_type(original.get("retAttrs", []), nullable_t)
        slot_name = f"$star${name}$0"
        carrier = owner(base + "$star")
        slot = method(carrier, slot_name)
        bridge = method(declaration, slot_name)
        require(carrier.get("kind") == "interface" and slot.get("abstract") is True
                and fqn(base + "$star") in declaration.get("interfaces", []),
                "base lost its declared carrier interface slot")
        for entry in (slot, bridge):
            require([p.get("type") for p in entry.get("params", [])] == [object_type]
                    and entry.get("ret") == object_type, "carrier slot and bridge disagree on ABI")
        forwarded = one([n for n in objects(bridge.get("body", []))
                         if n.get("k") == "callInstance"], "base carrier forwarding call")
        require(forwarded.get("ownerType") == fqn(base, [type_t])
                and forwarded.get("method") == name and forwarded.get("sig") == [object_type]
                and forwarded.get("ret") == object_type and forwarded.get("recv") == {"k": "this"}
                and forwarded.get("args") == [{"k": "local", "name": bridge["params"][0]["name"]}],
                "carrier bridge must forward to the original open base MethodDef")

        closure = one([t for t in types if any(
            field.get("name") == "__recv" and field.get("type") == fqn(derived)
            for field in t.get("fields", []))], "bound-reference receiver capture")
        invoke = method(closure, "invoke")
        call = one([n for n in objects(invoke.get("body", []))
                    if n.get("k") == "callInstance" and n.get("method") == slot_name],
                   "bound-reference carrier call")
        require(call.get("ownerType") == fqn(base + "$star") and call.get("virtual") is True
                and call.get("sig") == [object_type] and call.get("ret") == object_type
                and call.get("recv") == {"k": "field", "ownerType": fqn(closure["name"]),
                                         "recv": {"k": "this"}, "name": "__recv"},
                "bound reference lost its captured receiver or selected carrier slot")
        value = one([n for n in objects(invoke.get("body", []))
                     if n.get("k") == "var" and n.get("type") == nullable_int],
                    "nullable argument projection")
        require(value.get("init") == {"k": "cast", "type": nullable_int,
                                       "e": {"k": "local", "name": invoke["params"][0]["name"]}}
                and call.get("args") == [{"k": "cast", "type": object_type,
                                          "e": {"k": "local", "name": value["name"]}}],
                "bound reference lost its nullable argument conversion")
        one([n for n in objects(invoke.get("body", []))
             if n.get("k") == "cast" and n.get("type") == nullable_int and n.get("e") == call],
            "nullable result projection")

    # These two overloads still need UnsafeAccessor. Check their exact physical
    # selectors, not merely the source name or parameter count.
    pairs = [(t, m) for t in types for m in t.get("methods", [])
             if m.get("extern") and target_name(m) is not None]
    require(Counter(target_name(m) for _, m in pairs) == Counter({"select": 1, "selectMutable": 1}),
            "overload references lost their selected UnsafeAccessor targets")
    dictionary = fqn("System.Collections.Generic.IDictionary", [int_type, int_type])
    readonly = {"t": "mod", "req": False,
                "m": fqn("kotlin.collections.Map`2", [int_type, int_type]),
                "of": fqn("System.Object")}
    for target, argument in (("select", readonly), ("selectMutable", dictionary)):
        holder, accessor = one([p for p in pairs if target_name(p[1]) == target], target)
        expected = [fqn("ProtectedErasedOverloadCallable"), argument]
        require([p.get("type") for p in accessor.get("params", [])] == expected
                and accessor.get("ret") == int_type, "UnsafeAccessor changed the selected MethodDef signature")
        original = method(owner("ProtectedErasedOverloadCallable"), target)
        require([p.get("type") for p in original.get("params", [])] == [argument]
                and original.get("ret") == int_type, "UnsafeAccessor disagrees with its real target")
        call = one([n for n in objects(holder.get("methods", []))
                    if n.get("k") == "callStatic" and n.get("method") == accessor["name"]],
                   "overload UnsafeAccessor call")
        require(call.get("owner") == fqn(holder["name"]) and call.get("sig") == expected
                and call.get("ret") == int_type and len(call.get("args", [])) == 2,
                "overload call disagrees with its selected physical accessor")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: assert-inherited-protected-callable-cir.py <cir.json>")
    with open(sys.argv[1], encoding="utf-8") as stream:
        root = json.load(stream)
    validate(root)
    print("inherited callable references preserve base slots, nullable projections and exact overload selectors")
