#!/usr/bin/env python3
"""Check actual Kotlin encoder output against the maintained type schema."""
import json
from pathlib import Path
import sys
from xml.etree import ElementTree

root = Path(__file__).resolve().parents[2]
schema = json.loads((root / "docs/bir-cir.schema.json").read_text(encoding="utf-8"))
definitions = schema["$defs"]
expected = {}
for reference in definitions["type"]["oneOf"]:
    definition = definitions[reference["$ref"].removeprefix("#/$defs/")]
    if definition["x-dotkt-model"].get("kotlin", True):
        expected[definition["properties"]["t"]["const"]] = {
            name for name, field in definition["properties"].items() if field.get("x-dotkt-kotlin", True)
        }
expected["SourceClassifier"] = set(definitions["sourceClassifier"]["properties"])
seen = {}


def remember(tag, value):
    seen.setdefault(tag, set()).update(value)


def walk(value):
    if isinstance(value, dict):
        if "t" in value:
            remember(value["t"], value)
        if "classifier" in value:
            remember("SourceClassifier", value["classifier"])
        for child in value.values():
            walk(child)
    elif isinstance(value, list):
        for child in value:
            walk(child)


xml_path = root / "toolchain/kotc/build/test-results/test/TEST-kotc.bir.TypeNodeTest.xml"
report = ElementTree.parse(xml_path).getroot()
if int(report.attrib["tests"]) == 0 or any(int(report.attrib[key]) for key in ("skipped", "failures", "errors")):
    raise SystemExit("Kotlin type model tests did not complete successfully")
samples = 0
for output in report.iter("system-out"):
    for line in (output.text or "").splitlines():
        if line.startswith("IR_TYPE_SAMPLE "):
            walk(json.loads(line.removeprefix("IR_TYPE_SAMPLE ")))
            samples += 1
if seen != expected:
    raise SystemExit(f"Kotlin encoded schema properties mismatch: expected {expected}, got {seen}")
print(f"Kotlin encoded type schema coverage: {len(expected)} shapes, {samples} actual samples")
