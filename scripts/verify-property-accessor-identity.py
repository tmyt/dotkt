#!/usr/bin/env python3
"""Enforce #397's one-way Kotlin-property accessor identity boundary."""

from __future__ import annotations

import json
import re
import sys
import glob
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parent.parent
ROLES = {"get", "set"}
SEMANTIC_KEYS = {
    "propertyName",
    "propertyAccessor",
    "propertyAssociation",
    "kotlinAccessors",
    "kotlinPropertyAccessorCarrier",
    "physicalSlotBridge",
    "inheritedImplementation",
    "inheritedDefaultAccessors",
    "inheritedDefaultMethods",
}
PHYSICAL_PROPERTY_DESCRIPTOR_KEYS = {"getSig", "setSig", "getMethodArity", "setMethodArity"}


def walk(value: Any, path: str = "$") -> Iterable[tuple[str, dict[str, Any]]]:
    if isinstance(value, dict):
        yield path, value
        for key, child in value.items():
            yield from walk(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk(child, f"{path}[{index}]")


def validate_bir(file: Path, root: Any) -> list[str]:
    errors: list[str] = []
    for path, node in walk(root):
        physical_descriptors = sorted(PHYSICAL_PROPERTY_DESCRIPTOR_KEYS.intersection(node))
        if physical_descriptors:
            errors.append(
                f"{file}:{path}: bir2cir-only physical Property descriptor leaked into BIR: "
                + ", ".join(physical_descriptors)
            )
        has_name = "propertyName" in node
        has_role = "propertyAccessor" in node
        if has_name != has_role:
            errors.append(f"{file}:{path}: propertyName/propertyAccessor must be carried together")
        if has_name:
            # This is an explicitly-schema'd fact only when reached through its owning type-level array. Do not infer
            # its role from the incidental absence/presence of method fields on the object itself.
            inherited_default_fact = ".inheritedDefaultAccessors[" in path
            property_name = node.get("propertyName")
            role = node.get("propertyAccessor")
            if not isinstance(property_name, str) or not property_name:
                errors.append(f"{file}:{path}: propertyName must be a non-empty string")
            if role not in ROLES:
                errors.append(f"{file}:{path}: propertyAccessor must be get or set")
            if not inherited_default_fact and (
                not isinstance(node.get("propertyAssociation"), str) or not node["propertyAssociation"]
            ):
                errors.append(f"{file}:{path}: accessor declaration has no propertyAssociation")
            # A raw kotc method declaration is still in Kotlin vocabulary. Calls may acquire the same facts inside
            # bir2cir, but no such intermediate tree is persisted as a .bir.json artifact.
            if "params" in node and "static" in node and "k" not in node and node.get("name") != property_name:
                errors.append(
                    f"{file}:{path}: kotc accessor declaration name must remain its Kotlin property name"
                )

        if "kotlinAccessors" in node:
            roles = node.get("kotlinAccessors")
            if (
                not isinstance(roles, list)
                or not roles
                or any(role not in ROLES for role in roles)
                or len(roles) != len(set(roles))
            ):
                errors.append(f"{file}:{path}: kotlinAccessors must be a non-empty unique get/set list")
            if "get" in node or "set" in node:
                errors.append(f"{file}:{path}: kotc must not author physical Property accessor links")
            if not isinstance(node.get("propertyAssociation"), str) or not node["propertyAssociation"]:
                errors.append(f"{file}:{path}: Property record has no propertyAssociation")

        methods = node.get("methods")
        properties = node.get("properties")
        if not isinstance(methods, list) or not isinstance(properties, list):
            continue
        identities = [
            (method.get("propertyName"), method.get("propertyAccessor"), method.get("propertyAssociation"))
            for method in methods
            if isinstance(method, dict)
            and isinstance(method.get("propertyName"), str)
            and method.get("propertyAccessor") in ROLES
        ]
        for index, prop in enumerate(properties):
            if not isinstance(prop, dict):
                continue
            if "kotlinAccessors" not in prop:
                errors.append(
                    f"{file}:{path}.properties[{index}]: BIR Property record has no kotlinAccessors identity"
                )
                continue
            for role in prop.get("kotlinAccessors", []):
                identity = (prop.get("name"), role, prop.get("propertyAssociation"))
                count = identities.count(identity)
                if count != 1:
                    errors.append(
                        f"{file}:{path}.properties[{index}]: accessor association {identity!r} "
                        f"resolves {count} declaration(s)"
                    )
    return errors


def validate_cir(file: Path, root: Any) -> list[str]:
    errors: list[str] = []
    for path, node in walk(root):
        leaked = sorted(SEMANTIC_KEYS.intersection(node))
        if leaked:
            errors.append(f"{file}:{path}: BIR-only property identity leaked into CIR: {', '.join(leaked)}")
        # `index-get`/`index-set` identify Kotlin operator-function syntax, not Property accessors. They remain a
        # legitimate call-origin fact in CIR; only an unresolved Kotlin Property getter/setter crosses this boundary.
        if node.get("prop") in ROLES:
            errors.append(f"{file}:{path}: unresolved Kotlin property call reached CIR")
        for role in ROLES:
            physical_name = node.get(role)
            signature_key = f"{role}Sig"
            arity_key = f"{role}MethodArity"
            if isinstance(physical_name, str):
                if not isinstance(node.get(signature_key), list):
                    errors.append(
                        f"{file}:{path}: physical {role} accessor has no exact {signature_key}"
                    )
                arity = node.get(arity_key)
                if not isinstance(arity, int) or isinstance(arity, bool) or arity < 0:
                    errors.append(
                        f"{file}:{path}: physical {role} accessor has no valid {arity_key}"
                    )
            elif node.get(signature_key) is not None or node.get(arity_key) is not None:
                errors.append(
                    f"{file}:{path}: {signature_key}/{arity_key} exist without a physical {role} accessor"
                )
        # A member property's CLR signature excludes the implicit `this`: an empty getter signature (or a setter
        # containing only its value) therefore owns receiverless storage on this TypeDef. BackingFieldRename must have
        # moved that storage to its compiler-generated name before CIR is persisted. Indexed extension properties have
        # an explicit receiver in getSig/setSig and may legally coexist with a same-source-name field.
        if isinstance(node.get("fields"), list) and isinstance(node.get("properties"), list):
            fields_by_name = {
                field.get("name"): field
                for field in node.get("fields", [])
                if isinstance(field, dict) and isinstance(field.get("name"), str)
            }
            methods = [method for method in node.get("methods", []) if isinstance(method, dict)]
            for index, prop in enumerate(node.get("properties", [])):
                if not isinstance(prop, dict) or prop.get("name") not in fields_by_name:
                    continue
                getter = prop.get("getSig")
                setter = prop.get("setSig")
                if not (getter == [] or isinstance(setter, list) and len(setter) == 1):
                    continue
                role = "get" if getter == [] else "set"
                signature = getter if role == "get" else setter
                physical_name = prop.get(role)
                arity = prop.get(f"{role}MethodArity")
                accessors = [
                    method
                    for method in methods
                    if method.get("name") == physical_name
                    and len(method.get("params", [])) == len(signature)
                    and len(method.get("typeParams", [])) == arity
                ]
                field = fields_by_name[prop["name"]]
                # A reserved static singleton field may legally share its name with an unrelated instance property.
                # The collision BackingFieldRename owns is storage in the same static/instance domain as the accessor.
                if len(accessors) == 1 and bool(accessors[0].get("static")) == bool(field.get("static")):
                    errors.append(
                        f"{file}:{path}.properties[{index}]: receiverless CLR Property and field share "
                        f"metadata name {prop['name']!r}"
                    )
    return errors


FORBIDDEN_SOURCE_PATTERNS = (
    re.compile(r"StartsWith\(\s*\"(?:get|set)_\""),
    re.compile(r"\"(?:get|set)_\"\s*\+"),
    re.compile(r"\"(?:get|set)_\$"),
    re.compile(r'\$"(?:get|set)_\{'),
    re.compile(r'string\.Concat\(\s*"(?:get|set)_"'),
    # The removed reverse projection used variables named method/methodName/declarationName and then stripped the
    # accessor prefix with [4..] or Substring(4). Keep this narrow enough not to ban unrelated four-character slicing.
    re.compile(
        r"\b(?:method|methodName|declarationName|accessorName|name)\w*\s*"
        r"(?:\[\s*4\s*\.\.|\.Substring\(\s*4\s*\))"
    ),
)


def validate_sources() -> list[str]:
    """Heuristically reject physical-name parsing/allocation outside the one forward allocator.

    This deliberately supplements artifact-shape checks rather than pretending to parse C#/Kotlin. The exact two
    PhysicalName arms are allow-listed; every other allocator line remains subject to the same heuristic patterns.
    """
    errors: list[str] = []
    roots = (
        ROOT / "toolchain" / "bir2cir",
        ROOT / "toolchain" / "dll2klib",
        ROOT / "toolchain" / "ilemit",
        ROOT / "toolchain" / "kotc" / "src" / "main" / "kotlin" / "kotc" / "backend",
    )
    allocator = ROOT / "toolchain" / "bir2cir" / "KotlinPropertyAccessors.cs"
    for source_root in roots:
        for file in sorted(source_root.rglob("*")):
            if file.suffix not in {".cs", ".kt"}:
                continue
            for line_number, line in enumerate(file.read_text(encoding="utf-8").splitlines(), 1):
                if line.lstrip().startswith("//"):
                    continue
                if file == allocator and line.strip() in {
                    '"get" => "prop_get<" + sourceName + ">",',
                    '"set" => "prop_set<" + sourceName + ">",',
                }:
                    continue
                if any(pattern.search(line) for pattern in FORBIDDEN_SOURCE_PATTERNS):
                    errors.append(
                        f"{file.relative_to(ROOT)}:{line_number}: physical get_/set_ spelling is being parsed "
                        "or independently allocated"
                    )
    return errors


def main(argv: list[str]) -> int:
    errors = validate_sources()
    artifact_count = 0
    bir_accessor_count = 0
    bir_property_count = 0
    bir_call_count = 0
    seen: set[Path] = set()
    for raw in argv:
        # run-schema deliberately passes quoted patterns so an absent directory does not become a literal shell
        # argument.  Expand them here, exactly like verify-schema.py, rather than silently skipping the stdlib/build
        # corpus and letting the smaller explicit test-file set satisfy the anti-vacuity counters.
        for expanded in glob.glob(raw):
            file = Path(expanded)
            is_bir = file.name.endswith((".bir.json", ".bir-part.json"))
            is_cir = file.name.endswith(".cir.json")
            if file in seen or not file.is_file() or not (is_bir or is_cir):
                continue
            seen.add(file)
            artifact_count += 1
            try:
                root = json.loads(file.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exc:
                errors.append(f"{file}: cannot read JSON: {exc}")
                continue
            if is_bir:
                errors.extend(validate_bir(file, root))
                for _, node in walk(root):
                    if "propertyName" in node and "propertyAccessor" in node:
                        bir_accessor_count += 1
                    if "kotlinAccessors" in node:
                        bir_property_count += 1
                    if node.get("k") in {"callInstance", "callStatic"} and node.get("prop") in ROLES:
                        bir_call_count += 1
            else:
                errors.extend(validate_cir(file, root))

    if artifact_count == 0:
        errors.append("verify-property-accessor-identity: no BIR/CIR artifacts were supplied")
    if bir_accessor_count == 0 or bir_property_count == 0 or bir_call_count == 0:
        errors.append(
            "verify-property-accessor-identity: corpus is vacuous "
            f"(accessors={bir_accessor_count}, properties={bir_property_count}, calls={bir_call_count})"
        )
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        print(f"PROPERTY ACCESSOR IDENTITY: RED ({len(errors)} error(s))", file=sys.stderr)
        return 1
    print(
        "PROPERTY ACCESSOR IDENTITY: GREEN "
        f"({artifact_count} artifact(s), {bir_accessor_count} accessor(s), "
        f"{bir_property_count} property row(s), {bir_call_count} call(s))"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
