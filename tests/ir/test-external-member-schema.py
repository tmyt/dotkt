#!/usr/bin/env python3
"""Validate frozen foreign declaration facts without weakening lexical type slots."""
import copy
import importlib.util
import json
import pathlib
import sys

root = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "scripts"))
spec = importlib.util.spec_from_file_location("verify_schema", root / "scripts/verify-schema.py")
schema = importlib.util.module_from_spec(spec)
spec.loader.exec_module(schema)

owner = {"t": "fqn", "name": "System.IComparable`1", "args": [
    {"t": "fqn", "name": "Foreign.Box`1", "args": [{"t": "tv", "scope": "type", "i": 1}]}]}
call = {"k": "callInstance", "ownerType": {"t": "fqn", "name": "Foreign.Order"},
        "recv": {"k": "local", "name": "left"}, "method": "compareTo", "args": [],
        "externalMember": {"owner": json.dumps(owner), "member": "CompareTo"}}

def errors(node, phase="bir"):
    validator = schema.V()
    validator.walk(f"current.{phase}.json", node, "$")
    return validator.viol

assert not errors(call), errors(call)
physical = copy.deepcopy(call)
native_array = {"t": "array", "elem": {"t": "fqn", "name": "System.Int32"}, "rank": 2}
physical["externalMember"]["owner"] = json.dumps(dict(owner, args=[native_array]))
assert not errors(physical), errors(physical)

for malformed_owner in (None, owner, "not JSON", "null", "{}",
                        json.dumps(dict(owner, args=["Foreign.Box"])),
                        json.dumps(dict(owner, args=[{"t": "unknown"}]))):
    malformed = copy.deepcopy(call)
    malformed["externalMember"]["owner"] = malformed_owner
    assert errors(malformed), malformed
for field in ("owner", "member"):
    malformed = copy.deepcopy(call)
    del malformed["externalMember"][field]
    assert errors(malformed), malformed
for value in (None, [], "binding", dict(call["externalMember"], extra=True),
              dict(call["externalMember"], member=42)):
    malformed = dict(call, externalMember=value)
    assert errors(malformed), malformed
assert errors(call, "cir"), "foreign declaration binding must be consumed before CIR"
static_owner = {"t": "fqn", "name": "System.ArraySegment`1", "args": [{"t": "tv", "scope": "method", "i": 0}]}
static_call = {"k": "callStatic", "method": "op_Equality", "args": [],
               "externalMember": {"owner": json.dumps(static_owner), "member": "op_Equality"}}
assert not errors(static_call), errors(static_call)
assert errors(static_call, "cir"), "static binding must also be consumed before CIR"
assert errors({"k": "local", "name": "x", "externalMember": call["externalMember"]}), "binding requires a member call"
ordinary = copy.deepcopy(call)
del ordinary["externalMember"]
ordinary["ownerType"] = json.dumps(owner)
assert errors(ordinary), "ordinary owner strings must still fail"
ordinary["ownerType"] = native_array
assert errors(ordinary), "physical foreign types must not become ordinary BIR types"
print("external member schema: frozen typed payload, exact placement, malformed current inputs and lexical isolation OK")
