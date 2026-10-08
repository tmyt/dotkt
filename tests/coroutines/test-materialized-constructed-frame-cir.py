#!/usr/bin/env python3
"""Mutation-check the frame assertions against the current compiler's CIR output."""

import base64
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def objects(node):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from objects(value)
    elif isinstance(node, list):
        for value in node:
            yield from objects(value)


if len(sys.argv) != 2:
    raise SystemExit("usage: test-materialized-constructed-frame-cir.py <current CIR file>")

checker = Path(__file__).with_name("assert-materialized-constructed-frame-cir.py")
with open(sys.argv[1], encoding="utf-8") as stream:
    baseline = json.load(stream)


def declaration(root, name):
    return next(item for item in root["types"] if item.get("name") == name)


def allocation(root, owner, box):
    machine = declaration(root, owner)
    return next(item for item in objects(machine["methods"])
                if item.get("k") == "new" and item.get("type", {}).get("name") == box)


def source_method(root):
    return next(item for item in declaration(root, "MaterializedConstructedOwner")["methods"]
                if item.get("name") == "awaitList")


with tempfile.TemporaryDirectory(prefix="materialized-frame-check-") as directory:
    artifact = Path(directory) / "current.cir.json"

    def check(root, expected_error=None):
        artifact.write_text(json.dumps(root), encoding="utf-8")
        result = subprocess.run([sys.executable, str(checker), str(artifact)],
                                capture_output=True, text=True, check=False)
        if expected_error is None:
            if result.returncode != 0:
                raise SystemExit(f"positive frame assertion failed: {result.stderr}")
        elif result.returncode == 0 or expected_error not in result.stderr:
            raise SystemExit(f"frame mutation escaped its guard ({expected_error}): {result.stderr}")

    check(baseline)

    root = copy.deepcopy(baseline)
    declaration(root, "MaterializedConstructedOwner_awaitList$sm")["capturedTypeParams"].append("extra")
    check(root, "does not declare exactly its enclosing owner's T")

    root = copy.deepcopy(baseline)
    allocation(root, "MaterializedMixedOwner_awaitTaggedList$sm", "MaterializedMixedBox")["type"]["args"][1]["i"] = 1
    check(root, "wrong enclosing-owner specialization")

    root = copy.deepcopy(baseline)
    allocation(root, "MaterializedConstructedOwner_awaitList$sm", "MaterializedConstructedBox")["type"]["args"] = []
    check(root, "wrong enclosing-owner specialization")

    root = copy.deepcopy(baseline)
    source_method(root)["ret"]["args"][0]["m"]["args"][0]["i"] = 1
    check(root, "lost its exact List<T> result selector")

    root = copy.deepcopy(baseline)
    carrier = next(item for item in source_method(root)["attrs"]
                   if item.get("attr", {}).get("name") == "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute")
    source = json.loads(base64.b64decode(carrier["args"][1]["bytes"]))
    source["args"][0]["i"] = 1
    carrier["args"][1]["bytes"] = base64.b64encode(json.dumps(source).encode()).decode()
    check(root, "lost its source List<T> owner frame")

    root = copy.deepcopy(baseline)
    source_method(root)["attrs"] = [item for item in source_method(root)["attrs"]
                                  if item.get("attr", {}).get("name") != "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute"]
    check(root, "must preserve one source result carrier")

print("materialized frame checker: positive output + 6 malformed-current mutations rejected")
