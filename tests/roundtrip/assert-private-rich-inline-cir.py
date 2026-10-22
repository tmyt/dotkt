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


with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)
methods = [node for node in walk(root) if node.get("name") ==
           "unusedPrivateEnumsDoNotBlockIndependentLibrariesOrPublishedInlineBodies" and "body" in node]
assert len(methods) == 1, "missing private-enum inline regression method"
nodes = list(walk(methods[0]["body"]))
assert not any(node.get("method") == "publishedEnumCode" for node in nodes), \
    "PublishedApi regression fell back to an ordinary helper call"
entries = [node for node in nodes if node.get("k") == "staticField" and node.get("name") == "EXPORTED"
           and node.get("ownerType", {}).get("name") == "roundtrip.enumcollision.PublishedState"]
assert len(entries) == 1, "consumer did not splice the producer's PublishedApi enum access"
field = entries[0].get("fieldRef", {})
assert field.get("kind") == "field" and field.get("name") == "EXPORTED" and field.get("assembly"), \
    "spliced enum access lost its exact producer field declaration"
assert field.get("declaringType", {}).get("name") == "roundtrip.enumcollision.PublishedState"
print("consumer inline body retains the exact PublishedApi enum field")
