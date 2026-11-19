"""A public CLR const must fold to its decoded value in its own fragment, not its hidden namesake."""
import json
from pathlib import Path
import os
import subprocess
import sys
from tempfile import TemporaryDirectory

compiler, stdlib, producer = map(lambda arg: str(Path(arg).resolve()), sys.argv[1:])
if os.name == "nt":
    compiler += ".bat"
with TemporaryDirectory() as tmp:
    work = Path(tmp)
    hidden, common, leaf = (work / name for name in ("Hidden.kt", "Common.kt", "Leaf.kt"))
    # Both KLIB creation and separate common-fragment lookup consume the CLR stdlib.
    map_default = 'fun drop(m: MutableMap<String, Int>): Boolean = m.remove("item", 1)\n'
    hidden.write_text('package roundtrip.utf16literals\ninternal const val LOW_TEXT = "hidden"\n' + map_default)
    common.write_text('const val imported = roundtrip.utf16literals.LOW_TEXT\n' + map_default)
    leaf.write_text('fun result(): String = imported\n')
    library = work / "hidden.klib"
    subprocess.run([compiler, hidden, "-classpath", stdlib, "-module-name", "hidden", "-d", library],
                   check=True, env={**os.environ, "DOTKT_BUILD_KLIB": "1"})
    subprocess.run([compiler, common, leaf, "-classpath", stdlib, "-Xseparate-kmp-compilation",
                    "-Xfragments=common,clr", "-Xfragment-refines=clr:common",
                    f"-Xfragment-sources=common:{common}", f"-Xfragment-sources=clr:{leaf}",
                    f"-Xcommon-sources={common}", f"-Xfragment-dependency=common:{library}",
                    f"-Xfragment-dependency=common:{producer}", f"-Xfragment-dependency=common:{stdlib}",
                    "-P", "plugin:dotkt:bir-format=json", "-d", work / "bir"], check=True)
    fields = json.loads((work / "bir" / "Common.bir.json").read_text(encoding="utf-8"))["fields"]
    value = next(field["init"]["value"] for field in fields if field["name"] == "imported")
    if value != {"encoding": "utf16", "units": [0xDFFF]}:
        raise SystemExit(f"wrong imported constant: {value!r}")
