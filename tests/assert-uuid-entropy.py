"""Check the actual UUID entropy boundary without probabilistic randomness tests."""
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


assembly, runtime_dir, log = map(Path, sys.argv[1:])
module = json.loads((runtime_dir / "UuidClr.cir.json").read_text())
method, = [entry for entry in module["methods"] if entry["name"] == "secureRandomBytes"]
destination, = method["params"]
assert destination["type"] == {"t": "array", "elem": {"t": "fqn", "name": "System.SByte"}}
nodes = list(walk(method["body"]))
rng_type = {"t": "fqn", "name": "System.Security.Cryptography.RandomNumberGenerator"}
byte_array = {"t": "array", "elem": {"t": "fqn", "name": "System.Byte"}}
void = {"t": "fqn", "name": "void"}


def native_call(node, name):
    member = node.get("memberRef", {})
    return (member.get("assembly") == "System.Security.Cryptography"
            and member.get("declaringType") == rng_type and member.get("name") == name)


create, = [node for node in nodes if native_call(node, "Create")]
fill, = [node for node in nodes if native_call(node, "GetBytes")]
dispose, = [node for node in nodes if native_call(node, "Dispose")]
for node, name, convention, parameters, result in (
    (create, "Create", "static", [], rng_type),
    (fill, "GetBytes", "instance", [byte_array], void),
    (dispose, "Dispose", "instance", [], void),
):
    assert node["memberRef"] == {
        "kind": "method", "assembly": "System.Security.Cryptography",
        "declaringType": rng_type, "name": name, "genericArity": 0,
        "returnType": result, "callingConvention": convention, "parameterTypes": parameters,
    }, f"wrong native RNG signature: {name}"


def is_view(node):
    owner = node.get("owner") or node.get("calleeOwner") or {}
    return (node.get("k") == "callStatic" and node.get("method") == "asUByteArray"
            and not node.get("memberRef")
            and owner == {"t": "fqn", "name": "kotlin.collections._UArraysKt"}
            and node.get("sig") == [destination["type"]]
            and node.get("calleeRet") == byte_array and len(node["args"]) == 1)


definitions = {}
for node in nodes:
    if node.get("k") in ("var", "setLocal"):
        definitions.setdefault(node["name"], []).append(node.get("init", node.get("value")))


def origins(node, seen=frozenset()):
    kind = node.get("k")
    if kind == "local":
        name = node["name"]
        if name == destination["name"]:
            return {"destination"}
        assert name not in seen and name in definitions, f"unresolved entropy operand: {name}"
        return set().union(*(origins(value, seen | {name}) for value in definitions[name]))
    if node == create:
        return {"rng"}
    if is_view(node):
        return origins(node["args"][0], seen)
    if kind == "cast":
        return origins(node["e"], seen)
    if kind == "valueBlock":
        return origins(node["result"], seen)
    if kind == "cond":
        return origins(node["then"], seen) | origins(node["else"], seen)
    if kind == "throwExpr":
        return set()
    raise AssertionError(f"unexpected entropy value source: {kind}")


assert origins(fill["args"][0]) == {"destination"}, "RNG must fill the original storage, not a copy"
assert origins(fill["recv"]) == origins(dispose["recv"]) == {"rng"}, "wrong RNG resource"
protected, = [node for node in nodes if node.get("k") == "try"]
assert fill in list(walk(protected["body"])), "fill must execute inside the protected region"
assert dispose in list(walk(protected["finally"])), "RNG must be disposed in finally"
assert create not in list(walk(protected)), "factory failure must not dispose an uncreated RNG"
allowed_kinds = {"var", "setLocal", "local", "cast", "valueBlock", "cond", "throwExpr",
                 "try", "exprStmt", "callStatic", "clrStatic", "clrInstance"}
for node in nodes:
    assert "k" not in node or node["k"] in allowed_kinds, "unexpected entropy operation"
    if node.get("k") in ("callStatic", "clrStatic", "clrInstance"):
        assert node in (create, fill, dispose) or is_view(node), "unexpected entropy provider or copy operation"

verifiers = sorted((Path.home() / ".dotnet").rglob("ILVerify.dll"))
assert verifiers and assembly.is_file(), "missing verifier or runtime"
frameworks = [str(path) for path in Path("/usr/share/dotnet/shared/Microsoft.NETCore.App").iterdir() if path.is_dir()]
frameworks = subprocess.run(["sort", "-V"], input="\n".join(frameworks), text=True,
                           stdout=subprocess.PIPE, check=True).stdout.splitlines()
assert frameworks, "missing shared framework"
name = module["fileClass"] + ".secureRandomBytes"
command = ["dotnet", str(verifiers[-1]), str(assembly.resolve()), "-r", str(Path(frameworks[-1]) / "*.dll"),
           "-s", "System.Private.CoreLib", "--include", re.escape(name) + r"$", "--statistics", "--verbose"]
result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
log.write_text(result.stdout)
assert f"Verifying [DotKt.Stdlib]{name}\n" in result.stdout, "actual entropy body was not verified"
assert result.returncode == 0, f"actual UUID entropy body fails ILVerify: {log}"
assert f"All Classes and Methods in {assembly.resolve()} Verified." in result.stdout
assert not re.search(r"^\[(?:IL|MD)\]: Error", result.stdout, re.MULTILINE)
print("UUID entropy uses the native CSPRNG, shared destination storage and finally disposal; actual body verified")
