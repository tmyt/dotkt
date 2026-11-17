#!/usr/bin/env python3
"""Mixed arithmetic conversions are authored in CIR, not silently injected by ilemit."""
import json
import sys


def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from objects(child)


def fqn(name):
    return {"t": "fqn", "name": "System." + name}


with open(sys.argv[1], encoding="utf-8") as stream:
    cir = json.load(stream)
methods = {node["name"]: node for node in objects(cir)
           if "name" in node and "params" in node and "body" in node}


def binary(name, op):
    nodes = [node for node in objects(methods[name]["body"])
             if node.get("k") == "binOp" and node.get("op") == op]
    assert len(nodes) == 1, (name, nodes)
    return nodes[0]


def local_operand(node, method):
    # Primitive consumer boundaries may state the parameter's existing type.
    # They must not replace the separately required numeric widening conversion.
    casts = []
    while node.get("k") == "cast":
        casts.append(node.get("type"))
        node = node["e"]
    assert node.get("k") == "local", (method, node)
    parameters = {value["name"]: value["type"] for value in methods[method]["params"]}
    assert node["name"] in parameters, (method, node)
    assert all(target == parameters[node["name"]] for target in casts), (method, casts, node)


for name, op, converted, target in (
        ("numericLongLeft", "/", "rhs", "Int64"),
        ("numericLongRight", "+", "lhs", "Int64"),
        ("numericDoubleLeft", "/", "rhs", "Double"),
        ("numericDoubleRight", "+", "lhs", "Double"),
        ("numericFloatRight", "*", "lhs", "Single"),
        ("numericUnsigned", "/", "rhs", "UInt64")):
    node = binary(name, op)
    conversion = node[converted]
    assert conversion.get("k") == "conv" and conversion.get("to") == fqn(target), (name, node)
    local_operand(conversion["e"], name)
    other = node["rhs" if converted == "lhs" else "lhs"]
    local_operand(other, name)

addition = binary("numericChained", "+")
division = binary("numericChained", "/")
assert addition["lhs"]["k"] == "conv" and addition["lhs"]["to"] == fqn("Int64"), addition
assert division["lhs"]["k"] == "conv" and division["lhs"]["to"] == fqn("Double"), division
addition_input = division["lhs"]["e"]
while addition_input.get("k") == "cast" and addition_input.get("type") == fqn("Int64"):
    addition_input = addition_input["e"]
assert addition_input == addition, division

# Small storage types occupy Int32 evaluation-stack slots; Char's declared result still truncates explicitly.
small = binary("numericSmall", "+")
for side in ("lhs", "rhs"):
    assert small[side]["k"] == "conv" and small[side]["to"] == fqn("Int32"), small
char = binary("numericChar", "+")
assert char["lhs"]["k"] == "conv" and char["lhs"]["to"] == fqn("Int32"), char
assert any(node.get("k") == "conv" and node.get("to") == fqn("Char")
           and node.get("e") == char for node in objects(methods["numericChar"]["body"]))

shift = binary("numericShift", ">>>")
local_operand(shift["lhs"], "numericShift")
distance = shift["rhs"]
assert distance.get("k") == "binOp" and distance.get("op") == "&", shift
local_operand(distance["lhs"], "numericShift")
assert distance["rhs"].get("value") == {"a": "i32", "value": 63}, shift
assert not any(node.get("k") == "conv" for node in objects(distance)), shift
for name, op, side, target in (
        ("numericLoopRange", "+", "rhs", "Int64"),
        ("numericLoopUntil", "+", "lhs", "Int64"),
        ("numericLoopDownTo", "/", "lhs", "Double")):
    node = binary(name, op)
    assert node[side].get("k") == "conv" and node[side].get("to") == fqn(target), (name, node)
print("numeric operands: explicit widths, unsigned/narrow categories, nested operations and Int32 shift distance")
