"""Strictly verify the actual stdlib implementations behind the text-boundary regressions."""
import re
import subprocess
import sys
from pathlib import Path

assembly, log = map(Path, sys.argv[1:])
verifiers = sorted((Path.home() / ".dotnet").rglob("ILVerify.dll"))
assert verifiers, "install dotnet-ilverify before running the text-boundary gate"
runtimes = sorted(Path("/usr/share/dotnet/shared/Microsoft.NETCore.App").iterdir(),
                  key=lambda path: tuple(map(int, path.name.split("."))))
assert runtimes and assembly.is_file(), "missing stdlib runtime or shared framework"
owners = (r"kotlin\.text\.(?:IndentKt|_StringsKt)|kotlin\.time\.Duration|"
          r"dotkt\$ClrH_kotlin_text_StringBuilder")
command = ["dotnet", str(verifiers[-1]), str(assembly.resolve()),
           "-r", str(runtimes[-1] / "*.dll"), "-s", "System.Private.CoreLib",
           "--include", owners, "--statistics", "--verbose"]
result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
log.write_text(result.stdout)
required = ["kotlin.text.IndentKt.indentWidth", "kotlin.text.IndentKt.replaceIndentByMargin",
            "kotlin.text._StringsKt.filter", "kotlin.time.Duration.appendFractional",
            "dotkt$ClrH_kotlin_text_StringBuilder.append", "dotkt$ClrH_kotlin_text_StringBuilder.insert"]
for name in required:
    assert f"Verifying [DotKt.Stdlib]{name}\n" in result.stdout, f"stdlib method missing from verification: {name}"
assert result.returncode == 0, f"actual stdlib text bodies fail strict ILVerify; see {log} (exit {result.returncode})"
assert not re.search(r"^\[(?:IL|MD)\]: Error", result.stdout, re.MULTILINE)
assert f"All Classes and Methods in {assembly.resolve()} Verified." in result.stdout, "incomplete verification"
print("actual stdlib text owners strictly verified, including all six receiver/join regression methods")
