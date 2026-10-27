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


producer, bir, cir = [json.loads(Path(path).read_text()) for path in sys.argv[1:]]
method, = [method for method in producer["methods"] if method.get("name") == "inlineSequenceIndex"]
attribute, = [attribute for attribute in method.get("attrs", [])
              if attribute.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"]
assert attribute["args"][0]["value"] == "bir-json/1", "missing selected inline codec"
assert json.loads(base64.b64decode(attribute["args"][1]["bytes"])).get("body"), "missing producer inline body"
assert any(node.get("k") == "callInline" and node.get("callee", {}).get("name", "").endswith(".inlineSequenceIndex")
           for node in walk(bir)), "consumer stopped exercising the imported inline declaration"
assert any(node.get("name") == "importedReceiverBodiesAndInlineSplicesUseTheSelectedRepresentation"
           and node.get("body") for node in walk(cir)), "missing actual consumer method"
assert not any(node.get("method") == "inlineSequenceIndex" and node.get("k") in {"callStatic", "callInstance"}
               for node in walk(cir)), "consumer fell back to a non-spliced wrapper call"
print("CharSequence producer payload is consumed by actual cross-DLL inline splicing")
