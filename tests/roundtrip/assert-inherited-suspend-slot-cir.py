"""Inherited generic bodies must fill both exact suspend declaration slots."""
import json
import sys


def types(owner):
    for nested in owner.get("types", []):
        yield nested
        yield from types(nested)


def canonical(node):
    # The two CIR spellings denote the same ECMA object type. Keep every
    # modifier, generic frame and nested signature component otherwise exact.
    if isinstance(node, dict):
        result = {key: "System.Object" if key == "name" and value == "object"
                  else canonical(value) for key, value in node.items()}
        if result.get("name") == "System.Threading.Tasks.Task" and len(result.get("args", [])) == 1:
            result["name"] = "System.Threading.Tasks.Task`1"
        return result
    if isinstance(node, list):
        return [canonical(value) for value in node]
    return node


with open(sys.argv[1], encoding="utf-8") as source:
    producer = json.load(source)
with open(sys.argv[2], encoding="utf-8") as source:
    consumer = json.load(source)

factory_name = "roundtrip.inheritedsuspendcovariance.MethodFactory"
factory = next(owner for owner in types(producer) if owner["name"] == factory_name)
slots = {method["name"]: method for method in factory["methods"]
         if method["name"] in {"echo", "echo$dotkt_suspend"}}
assert len(slots) == 2, "Both public Task and cold declarations must exist"

for document, class_name in (
    (producer, "roundtrip.inheritedsuspendcovariance.ProducedMethod"),
    (consumer, "roundtriptests.inheritedsuspendcovariance.ConsumedMethod"),
):
    owner = next(owner for owner in types(document) if owner["name"] == class_name)
    for name, slot in slots.items():
        mappings = [(method, descriptor) for method in owner["methods"]
                    for descriptor in method.get("clrInterfaceImpls", [])
                    if descriptor["owner"]["name"] == factory_name
                    and descriptor["member"] == name]
        assert len(mappings) == 1, (class_name, name, "Missing or competing exact MethodImpl")
        body, descriptor = mappings[0]
        parameters = [parameter["type"] for parameter in slot["params"]]
        assert descriptor["arity"] == len(slot["typeParams"]) == 1
        assert canonical(descriptor["params"]) == canonical(parameters), (class_name, name, descriptor)
        assert canonical([parameter["type"] for parameter in body["params"]]) == canonical(parameters)
        assert canonical(descriptor["ret"]) == canonical(body["ret"]) == canonical(slot["ret"]), (
            class_name, name, descriptor["ret"], body["ret"], slot["ret"])
        assert parameters[0]["t"] == "mod", "Do not drop the selected declaration discriminator"

print("Inherited generic suspend bodies fill exact cold and Task interface slots")
