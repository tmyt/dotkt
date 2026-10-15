#!/usr/bin/env python3
"""Mutate a current-format generic call, preserving its complete member identity."""
import copy
import importlib.util
import pathlib
import sys

root = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "scripts"))
spec = importlib.util.spec_from_file_location("verify_schema", root / "scripts/verify-schema.py")
schema = importlib.util.module_from_spec(spec)
spec.loader.exec_module(schema)

call = {
    "k": "clrGenericInstance", "type": {"t": "fqn", "name": "Native.Counter"},
    "method": "Read", "recv": {"k": "local", "name": "counter"}, "args": [],
    "typeArgs": [{"t": "fqn", "name": "System.String"}],
    "receiverMode": "address", "dispatch": "call",
    "memberRef": {
        "kind": "method", "assembly": "Native", "declaringType": {"t": "fqn", "name": "Native.Counter"},
        "name": "Read", "callingConvention": "instance", "genericArity": 1,
        "parameterTypes": [], "returnType": {"t": "fqn", "name": "System.Int32"},
    },
}

def validate(node):
    validator = schema.V()
    validator.walk("current.cir.json", node, "$")
    return validator.viol

assert not validate(call), validate(call)
for mode in ("address", "value"):
    for dispatch in ("call", "callvirt", "constrained"):
        valid = dict(call, receiverMode=mode, dispatch=dispatch)
        assert not validate(valid), validate(valid)
for key, diagnostic in (("receiverMode", "address|value receiverMode"),
                        ("dispatch", "call|callvirt|constrained dispatch")):
    for missing in (True, False):
        malformed = copy.deepcopy(call)
        if missing:
            del malformed[key]
        else:
            malformed[key] = "invalid"
        errors = validate(malformed)
        assert any(diagnostic in error[2] for error in errors), errors
print("current generic receiver physical edge schema: OK")
