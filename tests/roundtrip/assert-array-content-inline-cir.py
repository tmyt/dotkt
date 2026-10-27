import base64
import json
import sys


def walk(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)


def read(path):
    with open(path, encoding="utf-8") as stream:
        return json.load(stream)


producer, consumer_bir, consumer_cir = map(read, sys.argv[1:])
wrappers = {"inlineDeepEquals", "inlineDeepHash", "inlineDeepString",
            "inlineFloatContents", "inlineDoubleContents"}
methods = [method for method in producer["methods"] if method.get("name") in wrappers]
assert len(methods) == 5 and {method["name"] for method in methods} == wrappers, \
    "missing or duplicated array-content inline producer methods"
for method in methods:
    payloads = [attr for attr in method.get("attrs", []) if attr.get("attr", {}).get("name") ==
                "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"]
    assert len(payloads) == 1, f"{method['name']} lost its actual inline payload"
    assert payloads[0]["args"][0]["value"] == "bir-json/1", "unexpected inline codec"
    payload = json.loads(base64.b64decode(payloads[0]["args"][1]["bytes"]))
    assert payload.get("body"), f"{method['name']} has no inline body"
calls = {node.get("callee", {}).get("name", "").rsplit(".", 1)[-1]
         for node in walk(consumer_bir) if node.get("k") == "callInline"}
assert wrappers <= calls, "consumer stopped exercising some array-content inline bodies"
required = {"checkImportedDeepContents", "importedFloatingEqualityRetainsStructuralNanAndSignedZeroRules",
            "importedRenderingPreservesCycleAndRepeatedChildSemantics"}
consumer_methods = [node for node in walk(consumer_cir) if node.get("name") in required and "body" in node]
assert {method["name"] for method in consumer_methods} == required, "missing inline consumer methods"
assert not any(node.get("method") in wrappers and node.get("k") in {"callStatic", "callInstance"}
               for node in walk(consumer_cir)), "array-content inline body fell back to an ordinary wrapper call"
print("all five array-content producer payloads are consumed by actual cross-DLL inline splicing")
