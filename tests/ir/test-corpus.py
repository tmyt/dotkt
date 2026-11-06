#!/usr/bin/env python3
"""Corpus selection and the real MSBuild recording hook, without recompiling the compiler corpus."""
import glob
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

from corpus import collect


ROOT = Path(__file__).resolve().parents[2]


class CorpusTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="dotkt-ir-corpus-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "repo [with spaces]"
        self.stdlib = set()
        self.current = set()
        for kind in ("bir", "cir"):
            self.stdlib.add(self.file(f"build/clr-stdlib/{kind}/Stdlib.{kind}.json"))
            self.current.add(self.file(f"producer/custom/rid/{kind}/Current.{kind}.json"))
        self.manifest = self.root / "build/nunit-ir-corpus.list"
        self.record(self.current)

    def file(self, name, contents="{}"):
        file = self.root / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(contents)
        return file

    def record(self, files):
        self.manifest.write_text("\n".join(sorted(map(str, files))) + "\n", encoding="utf-8-sig")

    def selected(self, phase):
        return {Path(file) for pattern in collect(self.root, phase) for file in glob.glob(pattern)}

    def test_only_recorded_paths_and_stdlib_are_selected_once(self):
        unrelated = [self.file("build/bir-scratch/Unrelated.bir.json", "not JSON"),
                     self.file("tests/unbuilt/obj/Release/net10.0/cir/Unrelated.cir.json", "not JSON")]
        self.record(list(self.current) * 2)
        self.assertEqual(self.selected("both"), self.current | self.stdlib)
        self.assertEqual(len(collect(self.root, "both")), 4)
        self.assertEqual(self.selected("cir"), {file for file in self.current | self.stdlib
                                                if file.name.endswith(".cir.json")})
        self.assertTrue(all(file.read_text() == "not JSON" for file in unrelated))

    def test_missing_empty_or_unfinished_cohort_is_not_a_default_corpus(self):
        self.manifest.rename(self.root / "build/nunit-ir-corpus.pending")
        with self.assertRaises(FileNotFoundError):
            collect(self.root, "both")
        self.manifest.write_text("")
        with self.assertRaises(ValueError):
            collect(self.root, "both")

    def test_invalid_or_missing_record_is_not_silently_skipped(self):
        for invalid in ("relative.cir.json", str(self.root / "missing.cir.json"),
                        str(self.file("other.txt"))):
            with self.subTest(record=invalid):
                self.record([*self.current, invalid])
                with self.assertRaises(ValueError):
                    collect(self.root, "both")

    def test_each_requested_phase_needs_test_and_stdlib_artifacts(self):
        self.record(file for file in self.current if file.name.endswith(".bir.json"))
        with self.assertRaises(ValueError):
            collect(self.root, "both")
        self.record(self.current)
        next(file for file in self.stdlib if file.name.endswith(".cir.json")).unlink()
        with self.assertRaises(ValueError):
            collect(self.root, "cir")

    def test_actual_msbuild_records_transitive_outputs_even_when_emit_is_skipped(self):
        child = ET.Element("Project", DefaultTargets="Build")
        properties = ET.SubElement(child, "PropertyGroup")
        for kind in ("Bir", "Cir"):
            ET.SubElement(properties, f"DotKtIntermediate{kind}Path").text = \
                f"$(MSBuildProjectDirectory)/custom output/{kind.lower()}"
        ET.SubElement(child, "Import", Project="$(CustomAfterMicrosoftCommonTargets)")
        ET.SubElement(child, "Target", Name="Build", DependsOnTargets="DotKtIlEmit")
        emit = ET.SubElement(child, "Target", Name="DotKtIlEmit", Inputs="input.kt", Outputs="emit.stamp")
        ET.SubElement(emit, "MakeDir", Directories="$(DotKtIntermediateBirPath);$(DotKtIntermediateCirPath)")
        for kind in ("Bir", "Cir"):
            ET.SubElement(emit, "WriteLinesToFile", File=f"$(DotKtIntermediate{kind}Path)/Output.{kind.lower()}.json",
                          Lines="{}", Overwrite="true")
        ET.SubElement(emit, "WriteLinesToFile", File="compile-count.txt", Lines="compiled", Overwrite="false")
        ET.SubElement(emit, "Touch", Files="emit.stamp", AlwaysCreate="true")
        producer = self.root / "producer/producer.proj"
        producer.parent.mkdir(parents=True, exist_ok=True)
        ET.ElementTree(child).write(producer, encoding="unicode")
        self.file("producer/input.kt", "current source")

        parent = ET.Element("Project", DefaultTargets="Build")
        ET.SubElement(parent, "Import", Project="$(CustomAfterMicrosoftCommonTargets)")
        references = ET.SubElement(parent, "ItemGroup")
        ET.SubElement(references, "ProjectReference", Include=str(producer))
        build = ET.SubElement(parent, "Target", Name="Build")
        ET.SubElement(build, "MSBuild", Projects="@(ProjectReference)", Targets="Build")
        project = self.root / "parent.proj"
        ET.ElementTree(parent).write(project, encoding="unicode")
        pending = self.root / "build/nunit-ir-corpus.pending"
        expected = {producer.parent / f"custom output/{kind}/Output.{kind}.json" for kind in ("bir", "cir")}
        for _ in range(2):
            result = subprocess.run(
                ["dotnet", "msbuild", str(project), "-nologo", "-v:quiet", "-m:1",
                 "-p:CustomAfterMicrosoftCommonTargets=" + str(ROOT / "tests/support/nunit/record-ir.targets"),
                 "-p:DotKtTestIrManifest=" + str(pending)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(set(map(Path, pending.read_text(encoding="utf-8-sig").splitlines())), expected)
            pending.unlink()
        self.assertEqual((producer.parent / "compile-count.txt").read_text().splitlines(), ["compiled"])


if __name__ == "__main__":
    unittest.main()
