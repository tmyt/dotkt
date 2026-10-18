#!/usr/bin/env python3
"""Keep the current valueBlock wire shape and its executable checks aligned."""
import copy
import importlib.util
import json
import pathlib
import sys
import unittest

root = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "scripts"))
spec = importlib.util.spec_from_file_location("verify_schema", root / "scripts/verify-schema.py")
schema = importlib.util.module_from_spec(spec)
spec.loader.exec_module(schema)


class ValueBlockSchemaTests(unittest.TestCase):
    def setUp(self):
        self.node = {
            "k": "valueBlock",
            "stmts": [{"k": "exprStmt", "expr": {
                "k": "const", "type": {"t": "fqn", "name": "kotlin.Int"}, "value": 1,
            }}],
            "result": {"k": "const", "type": {"t": "fqn", "name": "kotlin.Int"}, "value": 2},
        }

    def validate(self, node, phase):
        validator = schema.V()
        validator.walk(f"current.{phase}.json", node, "$")
        return validator.viol

    def test_documented_shape(self):
        with (root / "docs/bir-cir.schema.json").open() as source:
            document = json.load(source)
        variants = document["$defs"]["nodeByKind"]["oneOf"]
        shape, = [v for v in variants if v.get("properties", {}).get("k", {}).get("const") == "valueBlock"]
        self.assertEqual(set(shape["required"]), {"stmts", "result"})
        self.assertEqual(shape["properties"]["stmts"],
                         {"type": "array", "items": {"$ref": "#/$defs/node"}})
        self.assertEqual(shape["properties"]["result"], {"$ref": "#/$defs/node"})

    def test_current_shapes(self):
        empty = dict(self.node, stmts=[])
        nested = dict(self.node, result=copy.deepcopy(self.node))
        terminating = dict(self.node, result={"k": "throwExpr", "value": {
            "k": "const", "type": {"t": "fqn", "name": "kotlin.Throwable"}, "value": None,
        }})
        typed = dict(self.node, type={"t": "fqn", "name": "kotlin.Int"})
        for phase in ("bir", "cir"):
            for node in (self.node, empty, nested, terminating, typed):
                with self.subTest(phase=phase, node=node):
                    self.assertEqual(self.validate(node, phase), [])

    def test_missing_fields(self):
        for phase in ("bir", "cir"):
            for key in ("stmts", "result"):
                node = copy.deepcopy(self.node)
                del node[key]
                with self.subTest(phase=phase, key=key):
                    self.assertTrue(any(f"missing required field {key!r}" in e[2]
                                        for e in self.validate(node, phase)))

    def test_malformed_current_children(self):
        for phase in ("bir", "cir"):
            for key, values in (("stmts", (None, {}, 1)),
                                ("result", (None, [], 1, {}, {"k": 1}))):
                for value in values:
                    node = dict(self.node, **{key: value})
                    with self.subTest(phase=phase, key=key, value=value):
                        self.assertTrue(self.validate(node, phase))
            for value in (None, [], 1, {}, {"k": 1}):
                with self.subTest(phase=phase, statement=value):
                    self.assertTrue(self.validate(dict(self.node, stmts=[value]), phase))

    def test_children_are_recursively_validated(self):
        for phase in ("bir", "cir"):
            for key in ("stmts", "result"):
                node = copy.deepcopy(self.node)
                child = node[key][0] if key == "stmts" else node[key]
                child["k"] = "invalid"
                with self.subTest(phase=phase, key=key):
                    self.assertTrue(any("unknown node kind" in e[2] for e in self.validate(node, phase)))


if __name__ == "__main__":
    unittest.main()
