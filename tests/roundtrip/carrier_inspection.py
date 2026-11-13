"""Inspect executable carriers through the existing metadata inspector, not a second binary codec."""
import json
import subprocess


def executable_payload(attribute, inspector):
    codec = attribute["args"][0]["value"]
    assert codec == "bir-msgpack/1", "missing selected executable codec"
    return json.loads(subprocess.check_output([
        "dotnet", inspector, "--decode-carrier", codec, attribute["args"][1]["bytes"],
    ], text=True))
