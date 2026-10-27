import base64
import json
import sys
from pathlib import Path


def walk(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


polymorphic = sys.argv[1] == "--polymorphic"
paths = sys.argv[2:] if polymorphic else sys.argv[1:]
producer, bir, cir = [json.loads(Path(path).read_text()) for path in paths]
method, = [method for method in producer["methods"] if method.get("name") == "inlineSequenceIndex"]
attribute, = [attribute for attribute in method.get("attrs", [])
              if attribute.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"]
assert attribute["args"][0]["value"] == "bir-json/1", "missing selected inline codec"
assert json.loads(base64.b64decode(attribute["args"][1]["bytes"])).get("body"), "missing producer inline body"
assert any(node.get("k") == "callInline" and node.get("callee", {}).get("name", "").endswith(".inlineSequenceIndex")
           for node in walk(bir)), "consumer stopped exercising the imported inline declaration"
method_name = ("importedInlineReceiversAdaptStringsAndRetainCustomSequences" if polymorphic else
               "importedReceiverBodiesAndInlineSplicesUseTheSelectedRepresentation")
consumer_method, = [node for node in walk(cir) if node.get("name") == method_name and node.get("body")]
assert not any(node.get("method") == "inlineSequenceIndex" and node.get("k") in {"callStatic", "callInstance"}
               for node in walk(cir)), "consumer fell back to a non-spliced wrapper call"
if polymorphic:
    nodes = list(walk(consumer_method["body"]))
    locals_by_name = {node["name"]: node["type"] for node in nodes if node.get("k") == "var"}
    calls = [node for node in nodes if node.get("k") == "callInstance"
             and node.get("memberRef", {}).get("assembly") == "DotKt.Stdlib"
             and node["memberRef"].get("declaringType") == {"t": "fqn", "name": "dotkt$CharSequence"}]
    for name in ("prop_get<length>", "get"):
        selected = [node for node in calls if node["memberRef"].get("name") == name]
        assert any(node.get("recv", {}).get("k") == "new"
                   and node["recv"].get("type") == {"t": "fqn", "name": "dotkt$StringCharSequence"}
                   and node["recv"].get("argTypes") == [{"t": "fqn", "name": "System.String"}]
                   and len(node["recv"].get("args", [])) == 1
                   and node["recv"]["args"][0].get("k") == "local"
                   and locals_by_name.get(node["recv"]["args"][0]["name"]) == {"t": "fqn", "name": "System.String"}
                   for node in selected), f"spliced String {name} receiver lost its actual interface adapter"
        assert any(node.get("recv", {}).get("k") == "local"
                   and locals_by_name.get(node["recv"]["name"]) == {"t": "fqn", "name": "dotkt$CharSequence"}
                   for node in selected), f"spliced custom {name} receiver stopped retaining the interface value"
print("CharSequence producer payload is consumed by actual cross-DLL inline splicing")
