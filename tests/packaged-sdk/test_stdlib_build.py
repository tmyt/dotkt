#!/usr/bin/env python3
"""Exercise the real stdlib/pack scripts with compilers that leave partial outputs."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]
BUILDS = {
    "klib": ("build-stdlib-klib.sh", "need_fe_klib", "clr-stdlib-frontend-klib/kotlin-stdlib-clr-frontend.klib"),
    "ref": ("build-stdlib-ref.sh", "need_stdlib_ref", "clr-stdlib/dll/DotKt.Private.Stdlib.dll"),
    "rt": ("build-stdlib-rt.sh", "need_stdlib_rt", "clr-stdlib-rt/dll/DotKt.Stdlib.dll"),
}
STAGES = ("klib:kotc", "ref:kotc", "ref:bir2cir", "ref:ilemit", "rt:kotc", "rt:bir2cir", "rt:ilemit")

# Only compiler processes are replaced; script control flow, fingerprints and packaging remain real.
COMPILER = r'''#!/usr/bin/env python3
import os
from pathlib import Path
import sys
import time

args = sys.argv[1:]
if Path(sys.argv[0]).name == "kotc":
    output = Path(args[args.index("-d") + 1])
    stage = "kotc"
    if os.environ.get("DOTKT_BUILD_KLIB") == "1":
        kind = "klib"
    else:
        kind = "shared" if "clr-stdlib-shared" in output.parts else ("rt" if "clr-stdlib-rt" in output.parts else "ref")
        output = output / "partial.bir.json"
elif args[0] in ("build", "pack"):
    with open(os.environ["TRACE"], "a") as trace:
        trace.write(args[0] + "\n")
    sys.exit(0)
elif Path(args[0]).stem == "dll2klib":
    stage, kind = "dll2klib", "refs"
    output = Path(args[args.index("--out") + 1]) / "Framework.klib"
else:
    stage = Path(args[0]).stem
    output = Path(args[1])
    kind = "rt" if "clr-stdlib-rt" in output.parts else "ref"
    output = output / (args[2] + ".dll" if stage == "ilemit" else "partial.cir.json")
name = kind + ":" + stage
with open(os.environ["TRACE"], "a") as trace:
    trace.write(name + "\n")
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text("partial output\n")
if stage == "dll2klib" and os.environ.get("PROJECTION_STARTED"):
    sentinel = output.parent / ("owned-" + str(os.getpid()))
    sentinel.write_text("this invocation owns its generation\n")
    Path(os.environ["PROJECTION_STARTED"]).write_text(str(sentinel))
    deadline = time.monotonic() + 20
    while not Path(os.environ["PROJECTION_RELEASE"]).exists():
        if time.monotonic() >= deadline:
            sys.exit("projection release timed out")
        time.sleep(0.01)
    if not sentinel.exists():
        sys.exit("concurrent builder deleted an active projection")
if name == os.environ.get("FAIL_STAGE"):
    print("error: compiler failed after writing partial output", file=sys.stderr)
    sys.exit(41)
if stage == "dll2klib":
    output.write_text("complete projection\n")
'''


class StdlibBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="dotkt-stdlib-build-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "repo with spaces"
        self.root.mkdir()
        self.trace = self.root / "trace"
        for name in ("lib.sh", "pack-nuget.sh", "build-stdlib.sh", *(b[0] for b in BUILDS.values())):
            self.copy("scripts/" + name)
        # Pack guards and staging use the repository's current versions/templates, not test duplicates.
        shutil.copytree(ROOT / "packaging", self.root / "packaging",
                        ignore=shutil.ignore_patterns("bin", "obj", "tools", "lib"))
        for name in ("global.json", "README.md", "docs/user/getting-started.md"):
            self.copy(name)
        for name in ("common/src", "clr/common", "src", "unsigned/src"):
            (self.root / "libraries/stdlib" / name).mkdir(parents=True, exist_ok=True)
        self.write("libraries/stdlib/src/Empty.kt", "// fixture source\n")
        self.write("toolchain/kotc/build/install/kotc/bin/kotc", COMPILER, executable=True)
        self.write("bin/dotnet", COMPILER, executable=True)
        self.write("gradlew", "#!/usr/bin/env bash\nexit 0\n", executable=True)
        for tool in ("bir2cir", "ilemit", "dll2klib"):
            self.write(f"build/{tool}-bin/{tool}.dll", "fixture tool\n")
        self.write("refs/Framework.dll", "fixture reference\n")
        self.env = dict(os.environ, PATH=str(self.root / "bin") + os.pathsep + os.environ["PATH"],
                        TRACE=str(self.trace), DOTNET_REFPACK_DIR=str(self.root / "refs"),
                        FRAMEWORK_COMPILE_REFS=str(self.root / "refs/Framework.dll"))
        self.env.pop("DOTKT_BUILD_KLIB", None)
        self.env.pop("FAIL_STAGE", None)

    def copy(self, name):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)

    def write(self, name, content, executable=False):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content)
        if executable:
            target.chmod(0o755)

    def run_bash(self, command, fail_stage=""):
        return subprocess.run(["bash", "-c", command], cwd=self.root,
                              env=dict(self.env, FAIL_STAGE=fail_stage),
                              capture_output=True, text=True, timeout=30)

    def calls(self):
        return self.trace.read_text().splitlines() if self.trace.exists() else []

    def assert_failed_at(self, result, stage):
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = self.calls()
        self.assertIn(stage, calls)
        self.assertEqual(calls[-1], stage, calls)
        self.assertNotIn("pack", calls)
        # A completed reference-projection prerequisite may remain cached, but
        # a failed stdlib artifact itself must never receive a success stamp.
        for _, _, artifact in BUILDS.values():
            self.assertFalse(Path(str(self.root / "build" / artifact) + ".toolstamp").exists())
        kind = stage.split(":")[0]
        output = self.root / "build" / BUILDS.get(kind, BUILDS["ref"])[2]
        if stage.endswith("ilemit") or kind == "klib":
            self.assertTrue(output.is_file(), "the failing process must have left a partial artifact")

    def test_partial_failures_stop_direct_builds(self):
        for stage in STAGES:
            with self.subTest(stage=stage):
                kind = stage.split(":")[0]
                # A runtime build consumes an already-built reference assembly.
                self.write("build/" + BUILDS["ref"][2], "reference fixture\n")
                script = BUILDS[kind][0]
                self.assert_failed_at(self.run_bash(f"bash scripts/{script}" +
                                      (" --emit" if kind != "klib" else ""), stage), stage)

    def test_projection_failures_stop_all_stdlib_builders_without_stamp(self):
        for script in ("build-stdlib.sh", *(build[0] for build in BUILDS.values())):
            with self.subTest(script=script):
                result = self.run_bash(f"bash scripts/{script}", "refs:dll2klib")
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(self.calls()[-1], "refs:dll2klib")
                self.assertFalse(list((self.root / "build").rglob("*.toolstamp")))

    def test_partial_frontend_failures_stop_without_emit(self):
        for kind in ("ref", "rt"):
            with self.subTest(kind=kind):
                self.assert_failed_at(self.run_bash(f"bash scripts/{BUILDS[kind][0]}",
                                                   kind + ":kotc"), kind + ":kotc")

    def test_unified_build_stops_at_each_failed_stage(self):
        for stage in ("shared:kotc", *STAGES[2:4], *STAGES[5:]):
            with self.subTest(stage=stage):
                self.assert_failed_at(self.run_bash("bash scripts/build-stdlib.sh --emit", stage), stage)
        self.assert_failed_at(self.run_bash("bash scripts/build-stdlib.sh", "shared:kotc"), "shared:kotc")
        result = self.run_bash("bash scripts/build-stdlib.sh --emit")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_failed_builders_do_not_stamp_in_tested_shell_contexts(self):
        for stage in STAGES:
            with self.subTest(stage=stage):
                kind = stage.split(":")[0]
                # Existing stale artifacts must not turn a failed rebuild into a cache hit.
                self.write("build/" + BUILDS[kind][2], "stale artifact\n")
                self.write("build/" + BUILDS["ref"][2], "reference fixture\n")
                command = "source scripts/lib.sh; if " + BUILDS[kind][1] + "; then exit 0; else exit $?; fi"
                self.assert_failed_at(self.run_bash(command, stage), stage)

    def test_pack_never_packages_partial_builds(self):
        for stage in STAGES:
            with self.subTest(stage=stage):
                # Each attempt starts without successful stamps from an earlier pack.
                for stamp in (self.root / "build").rglob("*.toolstamp"):
                    stamp.unlink()
                result = self.run_bash("bash scripts/pack-nuget.sh", stage)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                calls = self.calls()
                self.assertEqual(calls[-1], stage, calls)
                self.assertNotIn("pack", calls)
                artifact = self.root / "build" / BUILDS[stage.split(":")[0]][2]
                self.assertFalse(Path(str(artifact) + ".toolstamp").exists())

    def test_successful_pack_stamps_and_reuses_artifacts(self):
        command = "bash scripts/pack-nuget.sh"
        result = self.run_bash(command)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual([c for c in self.calls() if ":" in c], ["refs:dll2klib", *STAGES])
        self.assertEqual(self.calls().count("pack"), 5)
        self.assertEqual(len(list((self.root / "build").rglob("*.toolstamp"))), 4)
        self.trace.unlink()
        result = self.run_bash(command)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse([c for c in self.calls() if ":" in c], self.calls())
        self.assertEqual(self.calls().count("pack"), 5)

    def test_framework_and_projector_changes_invalidate_the_dependent_artifacts(self):
        command = "bash scripts/pack-nuget.sh"
        for changed in ("refs/Framework.dll", "build/dll2klib-bin/dll2klib.dll"):
            with self.subTest(changed=changed):
                result = self.run_bash(command)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.trace.unlink()
                self.write(changed, "changed input\n")
                result = self.run_bash(command)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual([c for c in self.calls() if ":" in c], ["refs:dll2klib", *STAGES])
                self.trace.unlink()

    def test_parallel_frontend_and_reference_builds_do_not_delete_active_projection(self):
        started = self.root / "projection-started"
        release = self.root / "projection-release"
        first = subprocess.Popen(["bash", "scripts/build-stdlib-klib.sh"], cwd=self.root,
                                 env=dict(self.env, PROJECTION_STARTED=str(started), PROJECTION_RELEASE=str(release)),
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            deadline = time.monotonic() + 10
            while not started.exists() and first.poll() is None and time.monotonic() < deadline:
                time.sleep(0.01)
            self.assertTrue(started.exists(), "first projector did not reach the synchronization point")
            second = self.run_bash("bash scripts/build-stdlib-ref.sh --emit")
            self.assertEqual(second.returncode, 0, second.stdout + second.stderr)
        finally:
            release.touch()
            stdout, stderr = first.communicate(timeout=25)
        self.assertEqual(first.returncode, 0, stdout + stderr)

    def test_failed_new_catalog_cannot_poison_restored_successful_catalog(self):
        command = 'source scripts/lib.sh; stdlib_reference_args; printf "%s\\n" "${STDLIB_REFERENCE_ARGS[1]}"'
        first = self.run_bash(command)
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        self.assertEqual(Path(first.stdout.strip()).read_text(), "complete projection\n")
        self.write("refs/Framework.dll", "changed reference\n")
        failed = self.run_bash(command, "refs:dll2klib")
        self.assertNotEqual(failed.returncode, 0)
        self.write("refs/Framework.dll", "fixture reference\n")
        restored = self.run_bash(command)
        self.assertEqual(restored.returncode, 0, restored.stdout + restored.stderr)
        self.assertEqual(Path(restored.stdout.strip()).read_text(), "complete projection\n")


if __name__ == "__main__":
    unittest.main()
