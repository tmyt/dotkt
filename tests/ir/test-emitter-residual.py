#!/usr/bin/env python3
"""Calibrate the actual ownership scanner independently of residuals in today's emitter."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class EmitterResidualTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="dotkt-emitter-scanner-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "source with spaces"
        self.script = self.root / "tests/ir/check-emitter-residual.sh"
        self.script.parent.mkdir(parents=True)
        shutil.copy2(ROOT / "tests/ir/check-emitter-residual.sh", self.script)
        self.source = self.root / "toolchain/ilemit/Emitter.Delegates.cs"
        self.source.parent.mkdir(parents=True)
        self.source.write_text("sealed partial class Emitter { }\n")

    def run_scanner(self):
        return subprocess.run(["bash", str(self.script)], capture_output=True, text=True)

    def assert_verdict(self, expected):
        result = self.run_scanner()
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        self.assertIn("GREEN" if expected == 0 else "RED", result.stdout)

    def test_zero_residuals_and_lookup_comments_are_valid(self):
        self.source.write_text('// builder.GetMethod("Comment");\nsealed partial class Emitter { }\n')
        self.assert_verdict(0)

    def test_lookup_shapes_need_an_adjacent_justification(self):
        samples = ['type.GetMethod("Invoke");',
                   'type.GetMethod(add ? "Add" : "Remove");',
                   'type.GetField(node.GetProperty("name").GetString());',
                   'type.GetConstructor(parameters);',
                   'type.GetMethods().Single(m => m.Name == "Invoke");',
                   'member.Name != "Invoke";']
        for sample in samples:
            with self.subTest(sample=sample):
                self.source.write_text(sample + "\n")
                self.assert_verdict(1)
                self.source.write_text("// member-lookup-residual: fixture encoding control\n" + sample + "\n")
                self.assert_verdict(0)

    def test_minted_frame_requires_its_own_justification(self):
        self.source.write_text("builder.DefineGenericParameters(names);\n")
        self.assert_verdict(1)
        self.source.write_text("// delegate-frame-residual: fixture encoding control\n"
                               "builder.DefineGenericParameters(names);\n")
        self.assert_verdict(0)

    def test_missing_input_is_not_zero_residuals(self):
        self.source.unlink()
        self.assert_verdict(1)


if __name__ == "__main__":
    unittest.main()
