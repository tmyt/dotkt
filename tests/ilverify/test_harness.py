#!/usr/bin/env python3
"""Exercise the real shell classifier with a controlled verifier process, without .NET."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
STACK = "StackBufferTests::stackAllocationAndSpanInterop()"
BYREF = "ByRefParameterTests::byrefOfAStackSlotEvaluatesItsIndexOnce()"


class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="dotkt-ilverify-test-")
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.dll = self.work / "assembly with spaces.dll"
        self.dll.touch()
        self.bin = self.work / "bin"
        self.bin.mkdir()
        self.runtime = self.work / "runtime"
        self.runtime.mkdir()
        # Only discovery and the verifier executable are stubbed. The production shell parser,
        # exit handling, baseline classifier, and dead-entry audit all execute unmodified.
        self.command("find", 'printf "%s\\n" "$PROBE_WORK/ILVerify.dll"')
        self.command("ls", 'printf "%s\\n" "$PROBE_WORK/runtime"')
        self.command("dotnet", 'printf "%s\\n" "$PROBE_OUTPUT"; exit "$PROBE_STATUS"')

    def command(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\n" + body + "\n", encoding="utf-8")
        path.chmod(0o755)

    def finding(self, method=STACK, kind="Unverifiable"):
        return f"[IL]: Error [{kind}]: [{self.dll} : {method}][offset 0x00000000] finding."

    def complete(self, findings=()):
        if findings:
            return "\n".join([*findings, f"{len(findings)} Error(s) Verifying {self.dll}"])
        return f"All Classes and Methods in {self.dll} Verified."

    def run_probe(self, output, status, expected, *args, missing=False):
        env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                   PROBE_WORK=str(self.work), PROBE_OUTPUT=output, PROBE_STATUS=str(status))
        dll = self.work / "missing.dll" if missing else self.dll
        result = subprocess.run(["bash", str(ROOT / "tests/run-ilverify.sh"), *args, str(dll)],
                                env=env, text=True, capture_output=True)
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        if expected:
            self.assertNotIn("VERIFY  ", result.stdout)
        else:
            self.assertIn("VERIFY  ", result.stdout)
        return result.stdout

    def test_clean(self):
        self.run_probe(self.complete(), 0, 0)

    def test_crash_without_findings(self):
        out = self.run_probe("Unhandled exception. System.NullReferenceException", 1, 1)
        self.assertIn("NullReferenceException", out)

    def test_crash_after_allowed_finding(self):
        self.run_probe(self.finding() + "\nUnhandled exception. System.NullReferenceException", 1, 1)

    def test_crash_even_with_footer(self):
        self.run_probe(self.complete([self.finding()]) + "\nUnhandled exception.", 1, 1)

    def test_nonzero_without_findings(self):
        for status in (1, 2, 127, 134):
            with self.subTest(status=status):
                self.run_probe("failed to load reference", status, 1)

    def test_success_without_completion_is_not_verification(self):
        self.run_probe("", 0, 1)

    def test_wrong_assembly_footer(self):
        self.run_probe("All Classes and Methods in unrelated.dll Verified.", 0, 1)

    def test_allowed_findings(self):
        self.run_probe(self.complete([self.finding(), self.finding()]), 2, 0)

    def test_baseline_audit(self):
        self.run_probe(self.complete([self.finding(), self.finding(BYREF)]), 2, 0, "--audit-baseline")

    def test_stale_baseline(self):
        # A per-assembly VERIFY may precede the final dead-key audit failure.
        result = subprocess.run(["bash", str(ROOT / "tests/run-ilverify.sh"), "--audit-baseline", str(self.dll)],
                                env=dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                                         PROBE_WORK=str(self.work), PROBE_OUTPUT=self.complete(), PROBE_STATUS="0"),
                                text=True, capture_output=True)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("FIXED", result.stdout)

    def test_unexpected_il_finding_on_allowlisted_method(self):
        self.run_probe(self.complete([self.finding(kind="StackUnexpected")]), 2, 1)

    def test_metadata_finding(self):
        out = self.run_probe(self.complete(["[MD]: Error: missing interface implementation"]), 2, 1)
        self.assertIn("NEW-FAIL: [MD]", out)

    def test_metadata_cannot_borrow_il_allowance(self):
        self.run_probe(self.complete([f"[MD]: Error: Error [Unverifiable] {STACK}"]), 2, 1)

    def test_mixed_metadata_and_allowed_il(self):
        self.run_probe(self.complete([self.finding(), "[MD]: Error: missing interface implementation"]), 2, 1)

    def test_unknown_finding_format(self):
        self.run_probe(self.complete([self.finding(), "[NEW]: diagnostic"]), 2, 1)

    def test_inconsistent_status(self):
        self.run_probe(self.complete([self.finding()]), 0, 1)
        self.run_probe(self.complete(), 2, 1)

    def test_missing_input(self):
        self.run_probe(self.complete(), 0, 1, missing=True)

    def test_explicit_pointer_allowance(self):
        self.run_probe(self.complete([self.finding("Pointer::run()", "UnmanagedPointer")]), 2, 0,
                       "--allow-unmanaged-pointer=Pointer::run()")


if __name__ == "__main__":
    unittest.main()
