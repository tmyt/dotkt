"""Public suspend bridges retain the semantic/physical witness boundary."""
import base64
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    document = json.load(source)

expected = {
    "importedTypeCheck": 2,
    "importedExtensionCheck": 2,
    "matches": 2,
    "delayedTypeCheck": 3,
}
seen = set()


def declarations(owner):
    yield from owner.get("methods", [])
    for nested in owner.get("types", []):
        yield from declarations(nested)


for method in declarations(document):
    for attribute in method.get("attrs", []):
        if attribute["attr"].get("name") != "DotKt.Runtime.CompilerServices.KotlinDeclarationIdentityAttribute":
            continue
        arguments = attribute["args"]
        assert arguments[0]["value"] == "bir-json/1"
        identity = json.loads(base64.b64decode(arguments[1]["bytes"]))
        if identity["id"].endswith("|cold"):
            assert "signature" not in identity, "Cold entries must not publish another source declaration"
            continue
        name = identity["name"]
        if name not in expected:
            continue
        assert name not in seen, (name, "Duplicate public declaration")
        seen.add(name)
        assert identity["reified"] == [1], (name, identity)
        assert identity["nullableWitness"] == [1], (name, identity)
        assert len(identity["signature"]["params"]) == expected[name], (name, identity)
        assert len(method["params"]) == expected[name] + 1, (name, method["params"])
        assert method["params"][-1]["type"] == {"t": "fqn", "name": "System.Int32"}
        assert method["ret"]["name"] == "System.Threading.Tasks.Task", method["ret"]

assert seen == set(expected), (seen, expected)
print("Suspend Task bridges retain source reified indices and hidden witness metadata")
