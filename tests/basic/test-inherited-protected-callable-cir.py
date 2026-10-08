#!/usr/bin/env python3
"""Exercise the callable ABI guard with malformed copies of current CIR."""

import copy
import json
from pathlib import Path
import runpy
import sys


if len(sys.argv) != 2:
    raise SystemExit("usage: test-inherited-protected-callable-cir.py <current CIR file>")
checker = runpy.run_path(str(Path(__file__).with_name("assert-inherited-protected-callable-cir.py")))
with open(sys.argv[1], encoding="utf-8") as stream:
    baseline = json.load(stream)
validate = checker["validate"]
validate(baseline)


def declaration(root, name):
    return next(item for item in root["types"] if item.get("name") == name)


def method(owner, name):
    return next(item for item in owner["methods"] if item.get("name") == name)


def reject(mutation, expected_error):
    root = copy.deepcopy(baseline)
    mutation(root)
    try:
        validate(root)
    except ValueError as error:
        if expected_error not in str(error):
            raise SystemExit(f"mutation failed for the wrong reason: {error}")
    else:
        raise SystemExit(f"malformed-current mutation escaped: {expected_error}")


def lose_source_frame(root):
    method(declaration(root, "ProtectedNullableCallable"), "echo")["retAttrs"] = []


def specialize_open_owner(root):
    bridge = method(declaration(root, "ProtectedNullableCallable"), "$star$echo$0")
    call = next(node for node in checker["objects"](bridge["body"])
                if node.get("k") == "callInstance")
    call["ownerType"]["args"][0] = checker["fqn"]("System.Int32")


def lose_interface_slot(root):
    method(declaration(root, "ProtectedNullableCallable$star"), "$star$echo$0")["abstract"] = False


def lose_nullable_projection(root):
    projection = next(node for node in checker["objects"](root)
                      if node.get("k") == "cast"
                      and node.get("e", {}).get("method") == "$star$echo$0")
    projection["type"] = checker["fqn"]("System.Int32")


def lose_overload_selector(root):
    accessor = next(entry for owner in root["types"] for entry in owner.get("methods", [])
                    if entry.get("extern") and checker["target_name"](entry) == "select")
    accessor["params"][1]["type"] = checker["fqn"]("System.Object")


reject(lose_source_frame, "Kotlin nullable-generic source carrier")
reject(specialize_open_owner, "original open base MethodDef")
reject(lose_interface_slot, "declared carrier interface slot")
reject(lose_nullable_projection, "nullable result projection")
reject(lose_overload_selector, "selected MethodDef signature")
print("inherited callable checker: positive output + five malformed-current mutations rejected")
