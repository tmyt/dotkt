"""Check that re-synthesized default closures retain their representation metadata."""
import base64
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    document = json.load(source)

closures = [declaration for declaration in document["types"]
            if "roundtrip_defaultcompanion_DefaultCompanionCallbackKt$Closure" in declaration["name"]]
assert closures, "No materialized default closures were emitted"
source_arities = set()
for declaration in closures:
    attributes = [attribute for attribute in declaration.get("attrs", [])
                  if attribute["attr"].get("name") ==
                  "DotKt.Runtime.CompilerServices.KotlinSupertypesAttribute"]
    assert len(attributes) == 1, (declaration["name"], "Missing declaration-owned frame metadata")
    arguments = attributes[0]["args"]
    assert arguments[0]["value"] == "bir-json/1"
    frame = json.loads(base64.b64decode(arguments[1]["bytes"]))["nullableFrame"]
    source_arities.add(frame["sourceArity"])
    assert frame["nullable"] == [0], (declaration["name"], frame)
    assert frame["storage"] == frame["nullableStorage"] == []
    assert len(declaration["typeParams"]) == frame["sourceArity"] + 1
    assert frame["order"] == list(range(len(declaration["typeParams"])))
assert source_arities == {1, 2}, source_arities
print("Default closure source-to-physical frames are preserved")
