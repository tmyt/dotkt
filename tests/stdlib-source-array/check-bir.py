import json
import sys
from pathlib import Path


def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)


def fqn(name):
    return {"t": "fqn", "name": name}


root = Path(sys.argv[1])
probe = json.loads((root / "SourceArrayProbe.bir.json").read_text())
methods = {method["name"]: method for method in probe["methods"] if method["name"] != "chooseArray"}
families = ["Byte", "Short", "Int", "Long", "Float", "Double", "Char", "Boolean",
            "UByte", "UShort", "UInt", "ULong", "Generic"]
for family in families:
    method = methods[f"source{family}Array"]
    calls = [node for node in nodes(method["body"]) if node.get("k") == "callStatic"
             and node.get("method") == "consumeNativeArray"]
    assert len(calls) == 1, family
    call = calls[0]
    assert call["memberSignature"] == [fqn("System.Array")], family
    assert call["memberReturnType"] == method["ret"] == fqn("System.Array"), family
    assert call["args"][0]["k"] == "local", f"unexpected argument conversion: {family}"

for name, expected in [("sourceArrayOverload", "System.Array"), ("sourceObjectOverload", "kotlin.Any")]:
    calls = [node for node in nodes(methods[name]["body"]) if node.get("method") == "chooseArray"]
    assert len(calls) == 1 and calls[0]["memberSignature"] == [fqn(expected)], name

native = [node for node in nodes(methods["sourceArrayNativeLength"]["body"])
          if node.get("k") == "callInstance" and node.get("method") == "Length"]
assert len(native) == 1 and native[0]["ownerType"] == fqn("System.Array"), native
assert native[0]["recv"]["type"] == fqn("System.Array"), native
bound = [node for node in nodes(methods["sourceArrayBound"]["body"]) if node.get("method") == "boundedArray"]
assert len(bound) == 1 and bound[0]["typeArgs"] == [fqn("kotlin.CharArray")], bound
assert bound[0]["memberMethodTypeParams"][0]["constraints"] == [fqn("System.Array")], bound

expected = {"kotlin.Array"} | {"kotlin." + family + "Array" for family in families if family != "Generic"}
found = set()
for path in root.glob("*.bir.json"):
    for declaration in json.loads(path.read_text()).get("types", []):
        name = declaration["name"]
        if name not in expected:
            continue
        assert name not in found, f"duplicate source array declaration: {name}"
        found.add(name)
        assert declaration["base"] is None, f"native lookup base leaked into {name}"
        assert len(declaration.get("typeParams", [])) == (1 if name == "kotlin.Array" else 0), name
assert found == expected, (found, expected)
print("source-array assignability, exact calls, overloads, bounds and declaration ownership: OK")
