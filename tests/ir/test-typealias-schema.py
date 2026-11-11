"""Current declaration metadata must be checked independently of executable CLR type nodes."""
import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from typealias_schema import validate_aliases


class TypeAliasSchemaTests(unittest.TestCase):
    def setUp(self):
        self.alias = {
            "packageName": "sample", "ownerPath": [], "name": "Values", "visibility": "public",
            "typeParameters": [{"id": 0, "name": "T", "variance": "inv", "bounds": [], "annotations": []}],
            "underlyingType": {"kind": "parameter", "id": 0, "nullable": False, "suspend": False,
                               "definitelyNotNull": False, "arguments": [], "annotations": []},
            "annotations": [],
        }
        self.alias["expandedType"] = copy.deepcopy(self.alias["underlyingType"])

    def errors(self):
        errors = []
        validate_aliases([self.alias], "/typeAliases", lambda path, message: errors.append((path, message)))
        return errors

    def test_declaration_local_frame_is_valid(self):
        self.assertEqual(self.errors(), [])

    def test_unbound_parameter_is_malformed(self):
        self.alias["expandedType"]["id"] = 1
        self.assertTrue(any("does not belong" in message for _, message in self.errors()))

    def test_unknown_kind_is_malformed(self):
        self.alias["expandedType"]["kind"] = "typo"
        self.assertTrue(any("unknown alias declaration type kind" in message for _, message in self.errors()))

    def test_unknown_field_is_malformed(self):
        self.alias["expandedType"]["typo"] = True
        self.assertTrue(self.errors())

    def test_current_text_payload_preserves_only_code_units(self):
        value = {"kind": "string", "unsigned": False, "value": {"encoding": "utf16", "units": [0, 0xd800, 0xdc00, 65535]}}
        self.alias["annotations"] = [{"classifier": {"packageName": "sample", "ownerPath": [], "name": "Mark"},
                                      "arguments": [{"name": "text", "value": value}]}]
        self.assertEqual(self.errors(), [])
        for invalid in ([65536], [-1], [True], ["42"]):
            with self.subTest(invalid=invalid):
                value["value"]["units"] = invalid
                self.assertTrue(self.errors())


if __name__ == "__main__":
    unittest.main()
