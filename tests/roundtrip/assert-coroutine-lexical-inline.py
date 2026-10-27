"""Require actual imported inline receiver aliases/captures to share their operational carrier."""
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


producer, source, consumer = [json.loads(Path(path).read_text()) for path in sys.argv[1:]]
owner = "roundtrip.coroutinelexicalself.LexicalInlineOwner"
methods = ("aliasValue", "closureValue")
declaration, = [node for node in producer["types"] if node.get("name") == owner]
for name in methods:
    method, = [node for node in declaration["methods"] if node.get("name") == name]
    assert any(attr.get("attr", {}).get("name", "").endswith(".KotlinInlineAttribute")
               for attr in method.get("attrs", [])), f"missing producer inline payload: {name}"
    calls = [node for node in walk(source) if node.get("k") == "callInline"
             and node.get("callee", {}).get("name") == f"{owner}.{name}"]
    assert len(calls) == 2, f"missing Result/Continuation source inline calls: {name}"
    assert not any(node.get("method") == name or node.get("method", "").startswith(f"$star${name}$")
                   for node in walk(consumer)), f"ordinary wrapper call remains: {name}"

carrier = {"t": "fqn", "name": owner + "$star"}
temps = [node for node in walk(consumer) if node.get("k") == "var"
         and node.get("name", "").endswith("$this") and node.get("type") == carrier]
assert len(temps) == 4, "missing actual spliced receiver temporaries"
aliases = [node for node in walk(consumer) if node.get("k") == "var"
           and "$dotkt$local" in node.get("name", "") and node.get("type") == carrier]
assert len(aliases) == 2, "spliced alias locations did not retain their operational carrier"
captures = [field for declaration in consumer["types"] for field in declaration.get("fields", [])
            if field.get("name") == "__outer" and field.get("type") == carrier]
assert len(captures) == 2, "spliced closure receiver fields did not retain their operational carrier"
assert not any(node.get("t") == "fqn" and node.get("name") == owner for node in walk(consumer)), \
    "a constructed source owner leaked into an operational consumer slot"
print("both imported inline aliases/closures splice with consistent Result/Continuation receiver carriers")
