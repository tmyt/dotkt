import json
import sys
from pathlib import Path


def walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)


def is_type(node, name):
    return isinstance(node, dict) and node.get("t") == "fqn" and node.get("name") == name


def adapters(node):
    return [part for part in walk(node)
            if part.get("k") == "new" and is_type(part.get("type"), "dotkt$StringCharSequence")]


document = json.loads(Path(sys.argv[1]).read_text())
assert any(declaration.get("name") == "LocalStoreText"
           and any(is_type(interface, "dotkt$CharSequence")
                   for interface in declaration.get("interfaces", []))
           for declaration in document.get("types", [])), "fixture must declare a local CharSequence implementation"
fixture = next(declaration for declaration in document["types"]
               if declaration["name"] == "CharSequenceLocalStoreTests")
method = next(method for method in fixture["methods"]
              if method["name"] == "conditionalLocalStoresAndInlineResultsRetainCharSequenceRepresentation")
locals_by_name = {node["name"]: node["type"] for node in walk(method) if node.get("k") == "var"}
stores = [node for node in walk(method)
          if node.get("k") == "setLocal"
          and is_type(locals_by_name.get(node["name"]), "dotkt$CharSequence")]
conditional = [node for node in stores
               if node["value"].get("k") == "cond" and "$ret" not in node["name"]]
assert len(conditional) == 4, "both ordinary and nullable conditional stores must retain synthetic destinations"
assert all(adapters(node["value"]) for node in conditional), "each String branch needs its store-boundary adapter"
nullable = [node for node in conditional
            if any(part.get("k") == "valueBlock" for part in walk(node["value"]))]
assert len(nullable) == 2, "nullable stores must use null-preserving conversions"
assert all(any(part.get("k") == "const" and part.get("value") is None
               and is_type(part.get("type"), "dotkt$CharSequence")
               for part in walk(node["value"])) for node in nullable), "nullable branches must retain genuine null"
inline = [node for node in stores if "$ret" in node["name"] and adapters(node["value"])]
assert len(inline) == 2, "both inline early-return temporaries must remain synthetic and adapt the String fallback"
assert all(adapter["args"][0].get("value") == ""
           for node in inline for adapter in adapters(node["value"])), "inline empty returns must use the actual String value"
print("CharSequence local-store CIR: synthetic locals, conditional/null-preserving stores and inline results are adapted")
