#!/usr/bin/env python3
"""Run the real cohort shell functions with controlled restore/clean/build processes.

The process fixture models old package timestamps and a shared producer; it is not a substitute for the
real packaged-SDK project-graph benchmark, full NUnit discovery, or emitted-assembly ILVerify.
"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
DOTNET = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

root = Path(os.environ["COHORT_ROOT"])
if Path(os.environ["NUGET_PACKAGES"]) != root / "packages":
    sys.exit("compiler cohort escaped its private package cache")
stage, project = sys.argv[1], Path(sys.argv[2]).name
with (root / "trace").open("a") as trace:
    trace.write(json.dumps([stage, project, sys.argv[3:]]) + "\n")
if stage == os.environ.get("FAIL_STAGE"):
    sys.exit("controlled " + stage + " failure")
cache = root / "packages" / "dotkt.sdk" / "1.0" / "compiler"
if stage == "restore":
    if not cache.exists():
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text((root / "feed").read_text())
        os.utime(cache, (1, 1))
    sys.exit(0)

def visit(name):
    if name != "producer":
        visit("producer")
    output = root / "outputs" / name
    if stage == "clean":
        output.unlink(missing_ok=True)
    elif not output.exists():
        output.write_text(cache.read_text())
        with (root / "compiled").open("a") as trace:
            trace.write(name + ":" + cache.read_text() + "\n")

visit(project)
'''


class CohortTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="dotkt-nunit-cohort-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "repo with spaces"
        for name in ("bin", "build", "outputs", "packages/nunit"):
            (self.root / name).mkdir(parents=True, exist_ok=True)
        (self.root / "packages/nunit/retained").write_text("not a compiler package")
        (self.root / "feed").write_text("first")
        tool = self.root / "bin/dotnet"
        tool.write_text(DOTNET)
        tool.chmod(0o755)
        self.env = dict(os.environ, PATH=str(tool.parent) + os.pathsep + os.environ["PATH"],
                        COHORT_ROOT=str(self.root), NUGET_PACKAGES=str(self.root / "packages"))

    def invoke(self, command, failure=""):
        script = 'set -euo pipefail; source "$1"; root="$2"; ' + command
        return subprocess.run(["bash", "-c", script, "cohort-test",
                               str(ROOT / "tests/support/nunit/cohort.sh"), str(self.root)],
                              env=dict(self.env, FAIL_STAGE=failure), text=True, capture_output=True)

    def prepare(self, failure=""):
        return self.invoke('if ! nunit_prepare_cohort "$root" "$root/packages" Debug consumer-a consumer-b; '
                           'then exit 1; fi', failure)

    def build(self, project, failure=""):
        return self.invoke('if ! nunit_build_project "$root" Debug ' + project +
                           '; then exit 1; fi', failure)

    def success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def trace(self):
        return [json.loads(line) for line in (self.root / "trace").read_text().splitlines()]

    def test_shared_producer_once_per_cohort_and_same_version_repack(self):
        for version in ("first", "second"):
            with self.subTest(version=version):
                (self.root / "feed").write_text(version)
                self.env["NUGET_PACKAGES"] = str(self.root / "wrong-cache")
                self.success(self.invoke(
                    'nunit_prepare_cohort "$root" "$root/packages" Debug consumer-a consumer-b; '
                    '[[ -z $(find "$root/outputs" -type f -print -quit) ]]; '
                    'nunit_build_project "$root" Debug consumer-a; '
                    'nunit_build_project "$root" Debug consumer-b'))
                self.assertEqual((self.root / "outputs/producer").read_text(), version)
                self.assertEqual((self.root / "outputs/consumer-a").read_text(), version)
                self.assertEqual((self.root / "outputs/consumer-b").read_text(), version)
        compiled = (self.root / "compiled").read_text().splitlines()
        self.assertEqual(compiled, [name + ":" + version for version in ("first", "second")
                                   for name in ("producer", "consumer-a", "consumer-b")])
        self.assertTrue((self.root / "packages/nunit/retained").exists())
        actions = [row[0] for row in self.trace()]
        self.assertEqual(actions, ["restore", "restore", "clean", "clean", "build", "build"] * 2)
        for stage, _, args in self.trace():
            if stage == "build":
                self.assertIn("--no-restore", args)
                self.assertNotIn("--no-incremental", args)

    def test_restore_or_clean_failure_stops_preparation(self):
        for stage in ("restore", "clean"):
            with self.subTest(stage=stage):
                result = self.prepare(stage)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("controlled " + stage + " failure", result.stderr)
                self.assertFalse((self.root / "compiled").exists())
                self.assertFalse(any(row[0] == "build" for row in self.trace()))

    def test_consumer_build_failure_is_not_success(self):
        self.success(self.prepare())
        result = self.build("consumer-a", "build")
        self.assertNotEqual(result.returncode, 0)
        log = self.root / "build/nunit-consumer-a.build.log"
        self.assertIn("controlled build failure", log.read_text())
        self.assertFalse((self.root / "compiled").exists())


if __name__ == "__main__":
    unittest.main()
