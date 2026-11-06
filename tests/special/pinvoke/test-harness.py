#!/usr/bin/env python3
"""A package-cache cleanup failure must stop the real P/Invoke runner before any native build."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]


class PInvokeHarnessTests(unittest.TestCase):
    def test_cleanup_failure_is_not_suppressed(self):
        with tempfile.TemporaryDirectory(prefix="dotkt-pinvoke-harness-") as temporary:
            root = Path(temporary) / "repo with spaces"
            script = root / "tests/special/pinvoke/run.sh"
            script.parent.mkdir(parents=True)
            shutil.copy2(ROOT / "tests/special/pinvoke/run.sh", script)
            (root / "scripts").mkdir()
            shutil.copy2(ROOT / "scripts/lib.sh", root / "scripts/lib.sh")
            (root / "build/test-package-cache/dotkt.sdk").mkdir(parents=True)
            tools = root / "bin"
            tools.mkdir()
            for name, contents in {
                "find": '#!/usr/bin/env bash\necho "controlled package cleanup failure" >&2\nexit 17\n',
                "cc": '#!/usr/bin/env bash\necho "native build must not start" >&2\nexit 19\n',
            }.items():
                tool = tools / name
                tool.write_text(contents)
                tool.chmod(0o755)
            result = subprocess.run(["bash", str(script)], cwd=root,
                                    env=dict(os.environ, PATH=str(tools) + os.pathsep + os.environ["PATH"]),
                                    capture_output=True, text=True)
        self.assertEqual(result.returncode, 17, result.stdout + result.stderr)
        self.assertIn("controlled package cleanup failure", result.stderr)
        self.assertNotIn("native build must not start", result.stderr)


if __name__ == "__main__":
    unittest.main()
