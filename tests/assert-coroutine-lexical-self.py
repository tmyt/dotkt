"""Verify actual stdlib lexical self operands and their executable runtime bodies."""
import json
import re
import subprocess
import sys
from pathlib import Path


def walk(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


assembly, runtime_dir, reference_dir, log = map(Path, sys.argv[1:])
required = {
    "Result": ("kotlin.Result", ("getOrNull", "exceptionOrNull", "ToString", "GetHashCode", "Equals",
                               "prop_get<value>", "prop_get<isSuccess>", "prop_get<isFailure>", "$star$getOrNull$0")),
    "Continuation": ("kotlin.coroutines.Continuation", ("$star$resumeWith$0", "$star$prop_get<context>$1")),
}
verified_methods = []
for filename, (name, methods) in required.items():
    runtime = json.loads((runtime_dir / f"{filename}.cir.json").read_text())
    reference = json.loads((reference_dir / f"{filename}.cir.json").read_text())
    owner, = [node for node in runtime["types"] if node.get("name") == name]
    reference_owner, = [node for node in reference["types"] if node.get("name") == name]
    assert len(owner["typeParams"]) == len(reference_owner["typeParams"]) == 1, "source generic declaration changed"
    exact = {"t": "fqn", "name": name, "args": [{"t": "tv", "scope": "type", "i": 0}]}
    for method_name in methods:
        method, = [node for node in owner["methods"] if node.get("name") == method_name]
        assert method.get("body"), f"missing actual runtime body: {name}.{method_name}"
        own = [node for node in walk(method["body"]) if node.get("recv", {}).get("k") == "this"
               and "ownerType" in node]
        assert own, f"missing actual self operation: {name}.{method_name}"
        assert all(node["ownerType"] == exact for node in own), f"wrong lexical self owner: {name}.{method_name}"
        verified_methods.append(f"{name}`1.{method_name}")
    if filename == "Result":
        constructor, = owner["ctors"]
        store, = [node for node in walk(constructor["body"]) if node.get("k") == "setField"]
        assert store["recv"] == {"k": "this"} and store["ownerType"] == exact, "wrong constructor self field owner"
        verified_methods.append(f"{name}`1..ctor")
    else:
        resume, = [node for node in owner["methods"] if node.get("name") == "resumeWith"]
        assert [p["type"] for p in resume["params"]] == [{"t": "fqn", "name": "kotlin.Result$star"}], "protocol value ABI changed"
    assert not any("outer" in node for node in walk(runtime)), "transient enclosing role escaped into CIR"

verifiers = sorted((Path.home() / ".dotnet").rglob("ILVerify.dll"))
assert verifiers and assembly.is_file(), "missing verifier or runtime"
frameworks = [str(path) for path in Path("/usr/share/dotnet/shared/Microsoft.NETCore.App").iterdir() if path.is_dir()]
frameworks = subprocess.run(["sort", "-V"], input="\n".join(frameworks), text=True,
                           stdout=subprocess.PIPE, check=True).stdout.splitlines()
assert frameworks, "missing shared framework"
command = ["dotnet", str(verifiers[-1]), str(assembly.resolve()), "-r", str(Path(frameworks[-1]) / "*.dll"),
           "-s", "System.Private.CoreLib", "--include", r"kotlin\.Result`1(?:\.|$)|kotlin\.coroutines\.Continuation`1(?:\.|$)",
           "--statistics", "--verbose"]
result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
log.write_text(result.stdout)
for method in verified_methods:
    assert f"Verifying [DotKt.Stdlib]{method}\n" in result.stdout, f"actual runtime method not verified: {method}"
assert result.returncode == 0, f"stdlib lexical-self methods fail strict ILVerify: {log}, exit{result.returncode}"
assert not re.search(r"^\[(?:IL|MD)\]: Error", result.stdout, re.MULTILINE)
assert f"All Classes and Methods in {assembly.resolve()} Verified." in result.stdout, "incomplete verification"
print("all twelve runtime lexical-self methods strictly verified with exact authored owners and unchanged value ABI")
