"""Composition checks guard current phase-model changes, not old artifact formats."""
import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from bir_lowering_models import outputs
from bir_core_models import outputs as source_outputs
from bir_messagepack import outputs as binary_outputs
from bir_kotlin_messagepack import outputs as source_binary_outputs
from bir_dom_frontier import outputs as dom_frontier_outputs


class PhaseCompositionTests(unittest.TestCase):
    def setUp(self):
        self.definitions = copy.deepcopy(json.loads((ROOT / "docs/bir-cir.schema.json").read_text())["$defs"])

    def test_current_profiles_are_closed(self):
        self.assertEqual(2, len(outputs(ROOT, self.definitions)))

    def test_lowering_value_facts_stay_out_of_the_source_profile(self):
        lowering = "\n".join(outputs(ROOT, self.definitions).values())
        source = "\n".join(source_outputs(ROOT, self.definitions).values())
        source += "\n".join(source_binary_outputs(ROOT, self.definitions).values())
        self.assertIn("record EventPropertySourceFacts", lowering)
        self.assertIn("KotlinPropertyAccessorCarrier", lowering)
        self.assertNotIn("EventPropertySourceFacts", source)
        self.assertNotIn("kotlinPropertyAccessorCarrier", source)

    def test_binary_codecs_follow_the_same_declared_field(self):
        self.definitions["birFileDeclaration"]["properties"]["currentTransportFact"] = {"type": "integer"}
        model = "\n".join(outputs(ROOT, self.definitions).values())
        binary = "\n".join(binary_outputs(ROOT, self.definitions).values())
        source = "\n".join(source_binary_outputs(ROOT, self.definitions).values())
        frontier = "\n".join(dom_frontier_outputs(ROOT, self.definitions).values())
        self.assertIn("OptionalField<int> CurrentTransportFact", model)
        self.assertIn('case "currentTransportFact":', binary)
        self.assertIn("value.CurrentTransportFact.IsPresent", binary)
        self.assertIn("value.currentTransportFact?.let", source)
        self.assertIn('CurrentTransportFact = Field<int>(fields, "currentTransportFact"', frontier)

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
