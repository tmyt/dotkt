#!/usr/bin/env python3
"""An unrelated positive-fixture crash must redden the actual sanity runner."""
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class SanityHarnessTests(unittest.TestCase):
    def test_positive_fixture_crash_is_not_acceptance(self):
        real_dotnet = shutil.which("dotnet")
        self.assertIsNotNone(real_dotnet)
        with tempfile.TemporaryDirectory(prefix="dotkt-sanity-harness-") as temporary:
            shim = Path(temporary) / "dotnet"
            shim.write_text(
                '#!/usr/bin/env bash\n'
                'for argument in "$@"; do\n'
                '  case "$argument" in */selftest/accept-*.cir.json)\n'
                '    echo "controlled unrelated crash" >&2; exit 134 ;;\n'
                '  esac\n'
                'done\n'
                'exec ' + shlex.quote(real_dotnet) + ' "$@"\n')
            shim.chmod(0o755)
            result = subprocess.run(
                ["bash", "tests/ir/run-sanity.sh"], cwd=ROOT,
                env=dict(os.environ, PATH=temporary + os.pathsep + os.environ["PATH"]),
                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("controlled unrelated crash", result.stdout)
        self.assertIn("SANITY GATE: RED (self-test)", result.stdout)
        self.assertNotIn("SANITY GATE: GREEN", result.stdout)


if __name__ == "__main__":
    unittest.main()
