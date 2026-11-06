#!/usr/bin/env python3
"""Select only the successful NUnit cohort's recorded IR and the canonical reference stdlib."""
import glob
from pathlib import Path
import sys


def collect(root, phase):
    phases = ("bir", "cir") if phase == "both" else (phase,)
    if any(kind not in ("bir", "cir") for kind in phases):
        raise ValueError(f"unknown phase {phase!r}")
    manifest = root / "build/nunit-ir-corpus.list"
    records = manifest.read_text(encoding="utf-8-sig").splitlines()
    if not records:
        raise ValueError(f"empty corpus manifest: {manifest}")
    files = set()
    for record in records:
        file = Path(record)
        if not file.is_absolute() or not file.name.endswith((".bir.json", ".cir.json")) or not file.is_file():
            raise ValueError(f"invalid or missing recorded IR file: {record!r}")
        files.add(file)
    selected = set()
    for kind in phases:
        extension = f".{kind}.json"
        test_files = {file for file in files if file.name.endswith(extension)}
        stdlib_files = {file for file in (root / "build/clr-stdlib" / kind).glob(f"*{extension}")
                        if file.is_file()}
        if not test_files or not stdlib_files:
            raise ValueError(f"missing {kind} corpus from the NUnit cohort or reference stdlib")
        selected.update(test_files | stdlib_files)
    # The validators accept glob arguments. Escape literal recorded paths, including brackets in a workspace name.
    return [glob.escape(str(file)) for file in sorted(selected)]


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("usage: corpus.py <bir|cir|both>")
        print("\n".join(collect(Path(__file__).resolve().parents[2], sys.argv[1])))
    except (OSError, ValueError) as error:
        sys.exit(f"IR corpus: {error}; run make verify-nunit first, or pass explicit corpus paths to the runner")
