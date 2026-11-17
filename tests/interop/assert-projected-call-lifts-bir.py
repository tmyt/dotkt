#!/usr/bin/env python3
"""Every generated file-level delegate target in these call fixtures is used."""
import json
from pathlib import Path
import sys


def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from objects(child)


assert len(sys.argv) > 1, "at least one current fixture BIR is required"
for argument in sys.argv[1:]:
    document = json.loads(Path(argument).read_text())
    generated = [method for method in document["methods"] if method.get("generated")]
    assert generated, f"{argument}: the fixture must exercise file-level delegate lifting"
    declarations = {method["declarationId"] for method in generated}
    assert len(declarations) == len(generated), "generated declaration identities must be unique"
    references = {node["declarationId"] for node in objects(document)
                  if node.get("k") == "newDelegate"
                  and node.get("calleeOwner") == {"t": "fqn", "name": document["fileClass"]}}
    assert references == declarations, f"{argument}: orphaned lifts {declarations - references}; missing targets {references - declarations}"
print("projected call arguments leave one live declaration for each file-level delegate target")
