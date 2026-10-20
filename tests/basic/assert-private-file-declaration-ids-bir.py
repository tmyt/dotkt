#!/usr/bin/env python3
"""Check source-file private identity and selected call edges, independently of facade placement."""
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


def main():
    directory = Path(sys.argv[1])
    selected = {}
    for path in directory.glob("PrivateFileIdentity*.bir.json"):
        document = json.loads(path.read_text())
        names = {method["name"] for method in document["methods"]}
        for side in ("left", "right"):
            if f"{side}FilePrivateDirect" in names:
                assert side not in selected, f"duplicate {side} fixture"
                selected[side] = document
    assert set(selected) == {"left", "right"}, "both same-named source fixtures must be emitted"
    assert selected["left"]["fileClass"] == selected["right"]["fileClass"], "fixture facades must still coincide"
    identities = {"read": [], "execute": []}
    for side, document in selected.items():
        for name in identities:
            declaration = next(method for method in document["methods"] if method["name"] == name)
            identity = declaration["declarationId"]
            assert isinstance(identity, str) and identity, f"missing {side} private {name} identity"
            identities[name].append(identity)
            calls = [node for node in objects(document)
                     if (node.get("k") in {"callStatic", "callInstance", "newDelegate"} and node.get("method") == name)
                     or (node.get("k") == "callInline" and node["callee"]["name"] == f"privatefileidentity.probe.{name}")]
            assert calls, f"missing {side} selected private {name} call"
            assert all(node.get("declarationId") == identity for node in calls), f"{side} private {name} call changed declaration"
    assert all(len(set(pair)) == 2 for pair in identities.values()), "same facade must not conflate separate file-private declarations"
    print("source-file private identities, selected calls and inline targets are distinct and exact")


if __name__ == "__main__":
    main()
