"""Composition checks guard current phase-model changes, not old artifact formats."""
import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from bir_lowering_models import outputs


class PhaseCompositionTests(unittest.TestCase):
    def setUp(self):
        self.definitions = copy.deepcopy(json.loads((ROOT / "docs/bir-cir.schema.json").read_text())["$defs"])

    def test_current_profiles_are_closed(self):
        self.assertEqual(2, len(outputs(ROOT, self.definitions)))

    def test_consumed_node_fact_requires_a_type(self):
        self.definitions["cirNewClrNode"]["x-dotkt-model"]["sourceConsumed"].append("untypedCurrentFact")
        with self.assertRaisesRegex(ValueError, "Untyped consumed phase fields"):
            outputs(ROOT, self.definitions)

    def test_declaration_phase_difference_requires_an_explicit_contract(self):
        self.definitions["cirMethod"]["properties"]["ret"] = {"type": "boolean"}
        with self.assertRaisesRegex(ValueError, "Unaccounted declaration phase type"):
            outputs(ROOT, self.definitions)

    def test_new_profile_field_cannot_disappear_through_an_alias(self):
        self.definitions["cirFile"]["properties"]["unaccountedCurrentFact"] = {"type": "boolean"}
        with self.assertRaisesRegex(ValueError, "Unaccounted input-profile omission"):
            outputs(ROOT, self.definitions)


if __name__ == "__main__":
    unittest.main()
