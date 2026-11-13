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
from bir_cir_projection import outputs as cir_projection_outputs
import importlib.util

generator_spec = importlib.util.spec_from_file_location("ir_generator", ROOT / "scripts/generate-ir-model.py")
generator = importlib.util.module_from_spec(generator_spec)
generator_spec.loader.exec_module(generator)


class PhaseCompositionTests(unittest.TestCase):
    def setUp(self):
        self.definitions = copy.deepcopy(json.loads((ROOT / "docs/bir-cir.schema.json").read_text())["$defs"])

    def test_current_profiles_are_closed(self):
        self.assertEqual(2, len(outputs(ROOT, self.definitions)))

    def test_generic_frame_roles_follow_the_owning_field_contract(self):
        model = next(content for path, content in outputs(ROOT, self.definitions).items()
                     if path.name == "BirGraph.Model.Generated.cs")

        def record(name):
            return model.split("internal sealed record " + name + " :", 1)[1].split("internal sealed record ", 1)[0]

        self.assertRegex(record("BirNew"), r"ArgTypes = .*BirGenericFrameRole.Lexical")
        self.assertRegex(record("BirCallStatic"), r"ArgTypes = .*BirGenericFrameRole.ReferencedDeclaration")
        self.assertRegex(record("BirLocalFunction"), r"Decl = .*BirGenericFrameRole.LocalDeclaration")
        self.assertRegex(record("BirFieldDeclaration"), r"KotlinType = .*BirGenericFrameRole.StorageSourceType")
        self.definitions["birCallStatic"]["properties"]["sig"]["x-dotkt-frame-role"] = "UnknownFrameRole"
        with self.assertRaisesRegex(ValueError, "Unknown generic-frame role"):
            outputs(ROOT, self.definitions)

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

    def test_final_projection_requires_explicit_consumption_of_lowering_facts(self):
        self.definitions["birCallInstance"]["x-dotkt-model"]["loweringProperties"]["currentPhaseFact"] = {"type": "integer"}

        def projection():
            return "\n".join(cir_projection_outputs(ROOT, self.definitions, generator.model_type,
                generator.non_null_field, generator.allows_null).values())

        self.assertIn("value.CurrentPhaseFact.IsPresent", projection())
        self.definitions["cirCallInstanceNode"]["x-dotkt-model"]["sourceConsumed"].append("currentPhaseFact")
        self.assertNotIn("value.CurrentPhaseFact.IsPresent", projection())
        self.definitions["cirFile"]["properties"]["currentPhysicalFact"] = {"type": "integer"}
        self.definitions["birFileDeclaration"]["properties"]["currentPhysicalFact"] = {"type": "integer"}
        self.assertIn("CurrentPhysicalFact = ProjectOptional(value.CurrentPhysicalFact", projection())


if __name__ == "__main__":
    unittest.main()
