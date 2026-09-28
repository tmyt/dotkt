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
    "witnessFreeCheck": 1,
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
            assert set(identity) == {"id", "name"}, "Cold entries must not publish another source declaration"
            continue
        name = identity["name"]
        if name not in expected:
            continue
        assert name not in seen, (name, "Duplicate public declaration")
        seen.add(name)
        assert identity["reified"] == [1], (name, identity)
        witnesses = [] if name == "witnessFreeCheck" else [1]
        assert identity.get("nullableWitness", []) == witnesses, (name, identity)
        assert len(identity["signature"]["params"]) == expected[name], (name, identity)
        assert len(method["params"]) == expected[name] + len(witnesses), (name, method["params"])
        if witnesses:
            assert method["params"][-1]["type"] == {"t": "fqn", "name": "System.Int32"}
        assert method["ret"] == {
            "t": "fqn", "name": "System.Threading.Tasks.Task",
            "args": [{"t": "fqn", "name": "System.Boolean"}],
        }, method["ret"]

assert seen == set(expected), (seen, expected)

with open(sys.argv[2], encoding="utf-8") as source:
    consumer = json.load(source)


def objects(node):
    if isinstance(node, dict):
        yield node
        for child in node.values():
            yield from objects(child)
    elif isinstance(node, list):
        for child in node:
            yield from objects(child)


cold_names = {name + "$dotkt_suspend": name for name in expected}
called = set()
for call in objects(consumer):
    name = cold_names.get(call.get("method"))
    if name is None:
        continue
    called.add(name)
    witness_count = 0 if name == "witnessFreeCheck" else 1
    parameter_count = expected[name] + witness_count + 1
    assert len(call["args"]) == parameter_count, (name, call)
    target = call["memberRef"]
    assert target["name"] == call["method"]
    assert len(target["parameterTypes"]) == parameter_count, (name, target)
    if witness_count:
        assert target["parameterTypes"][-2] == {"t": "fqn", "name": "System.Int32"}
    assert target["parameterTypes"][-1]["name"] == "kotlin.coroutines.Continuation$star"
assert called == set(expected), (called, "Cross-DLL cold calls must actually be exercised")
print("Suspend Task bridges retain source reified indices and hidden witness metadata")
