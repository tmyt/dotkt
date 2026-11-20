"""Inspect carriers through their shared typed codecs in the existing C# inspector."""
import atexit
import json
import os
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

_record_reader = None

def metadata_record(arguments, inspector=None):
    """Read declaration records with the production codec; reuse one inspector per fixture."""
    global _record_reader
    if _record_reader is None:
        _record_reader = subprocess.Popen(
            ["dotnet", inspector or os.environ["METADATA_INSPECTOR_DLL"], "--decode-metadata-lines"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        def finish():
            _record_reader.stdin.close()
            _record_reader.wait()
        atexit.register(finish)
    request = [arguments[0]["value"], arguments[1]["bytes"]]
    print(json.dumps(request), file=_record_reader.stdin, flush=True)
    return json.loads(_record_reader.stdout.readline())
