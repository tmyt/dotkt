"""Inspect carriers through their shared typed codecs in the existing C# inspector."""
import json
import subprocess


def type_payloads(attributes, inspector):
    """Decode the fixture's selected TypeNode leaves in one process, preserving exact attributes."""
    attributes = list(attributes)
    if not attributes:
        return {}
    assert all(len(attribute["args"]) == 2 for attribute in attributes), "invalid TypeNode carrier envelope"
    requests = [[attribute["args"][0]["value"], attribute["args"][1]["bytes"]]
                for attribute in attributes]
    result = json.loads(subprocess.check_output([
        "dotnet", inspector, "--decode-type-carriers",
    ], input=json.dumps(requests), text=True))
    assert len(result) == len(attributes), "typed carrier inspection lost a requested occurrence"
    return {id(attribute): payload for attribute, payload in zip(attributes, result)}


def executable_payload(attribute, inspector):
    codec = attribute["args"][0]["value"]
    assert codec == "bir-msgpack/1", "missing selected executable codec"
    return json.loads(subprocess.check_output([
        "dotnet", inspector, "--decode-carrier", codec, attribute["args"][1]["bytes"],
    ], text=True))
