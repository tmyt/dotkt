#!/usr/bin/env python3
"""Validate actual Kotlin source-metadata encoder output, including all closed unions."""
import json
from pathlib import Path
import sys
from xml.etree import ElementTree

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "scripts"))
from typealias_schema import validate_aliases

schema = json.loads((root / "docs/kotlin-typealias.schema.json").read_text())
report = ElementTree.parse(root / "toolchain/kotc/build/test-results/test/TEST-kotc.bir.SourceMetadataTest.xml").getroot()
if int(report.attrib["tests"]) == 0 or any(int(report.attrib[key]) for key in ("skipped", "failures", "errors")):
    raise SystemExit("Kotlin source metadata tests did not complete successfully")
samples = [json.loads(line.removeprefix("IR_SOURCE_METADATA_SAMPLE "))
           for output in report.iter("system-out") for line in (output.text or "").splitlines()
           if line.startswith("IR_SOURCE_METADATA_SAMPLE ")]
errors = []
validate_aliases(samples, "/typeAliases", lambda path, message: errors.append((path, message)))
if errors:
    raise SystemExit(f"Actual typed source metadata violates the schema: {errors}")

def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from objects(child)

nodes = list(objects(samples))
actual_types = {node["kind"] for node in nodes if "nullable" in node}
actual_arguments = {node["variance"] for node in nodes if "variance" in node and "bounds" not in node}
actual_values = {node["kind"] for node in nodes if "unsigned" in node}
expected_values = {tag for branch in schema["$defs"]["value"]["oneOf"]
                   for tag in branch["properties"]["kind"].get("enum", [branch["properties"]["kind"].get("const")])}
assert actual_types == set(schema["$defs"]["type"]["properties"]["kind"]["enum"]), actual_types
assert actual_arguments == set(schema["$defs"]["argument"]["properties"]["variance"]["enum"]), actual_arguments
assert actual_values == expected_values, actual_values
values = {node["kind"]: node for node in nodes if "unsigned" in node}
assert values["char"]["value"] == 0xd800
assert values["long"]["value"] == -1 and values["long"]["unsigned"] is True
assert values["float"]["bits"] == 0xffc00017 - (1 << 32)
assert values["double"]["bits"] == -(1 << 63)
assert any(node.get("kind") == "string" and node["value"] == 'quote"\\\n\x01κ😀' for node in nodes)
assert any(node.get("variance") == "star" and "type" not in node for node in nodes)
assert any(node.get("kind") == "parameter" and "classifier" not in node and "outerType" not in node for node in nodes)
print(f"Kotlin source metadata schema coverage: {len(actual_types)} types, {len(actual_arguments)} projections, {len(actual_values)} constants")
