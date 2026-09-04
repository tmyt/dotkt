#!/usr/bin/env python3
"""Assert inherited-default facts use a vararg declaration's array slot."""

import json
import sys


if len(sys.argv) != 2:
    raise SystemExit("usage: assert-inherited-default-vararg-bir.py <VarargOmissionTests.bir.json>")

with open(sys.argv[1], encoding="utf-8") as stream:
    root = json.load(stream)

owners = [item for item in root.get("types", []) if item.get("name") == "VarargOmissionDefaultImpl"]
if len(owners) != 1:
    raise SystemExit(f"found {len(owners)} VarargOmissionDefaultImpl types, expected 1")

facts = owners[0].get("inheritedDefaultMethods", [])
facts = [fact for fact in facts if fact.get("member") == "join"]
expected = [{"t": "array", "elem": {"t": "fqn", "name": "kotlin.String"}}]
if len(facts) != 1 or facts[0].get("params") != expected:
    raise SystemExit(f"inherited default vararg fact does not carry Array<String>: {facts!r}")

print("inherited default vararg fact carries its array slot")
