#!/usr/bin/env python3
"""Generate structural IR models from the maintained logical schema.

Semantic operations, equality and representation choices stay in their owning
compiler layers. This initial domain is TypeNode; other typed domains use the
same schema authority as they migrate. No generic extension-data bag is emitted.
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "docs/bir-cir.schema.json"


def model_type(field, definitions, language):
    if "$ref" in field:
        name = field["$ref"].removeprefix("#/$defs/")
        return "TypeNode" if name == "type" else definitions[name]["x-dotkt-model"]["name"]
    kind = field.get("type", "string" if "enum" in field else None)
    if kind == "array":
        element = model_type(field["items"], definitions, language)
        return element + "[]" if language == "csharp" else f"List<{element}>"
    return {"csharp": {"string": "string", "integer": "int", "boolean": "bool"},
            "kotlin": {"string": "String", "integer": "Int", "boolean": "Boolean"}}[language][kind]


def fields(definition, language):
    return [(name, field) for name, field in definition["properties"].items()
            if name != "t" and field.get(f"x-dotkt-{language}", True)]


def parameter(name, field, definition, definitions, language):
    required = name in definition.get("required", [])
    value_type = model_type(field, definitions, language)
    if language == "csharp":
        display = field.get("x-dotkt-csharp-name", name[0].upper() + name[1:])
        return f"{value_type}{'' if required else '?'} {display}" + ("" if required else " = null")
    return f"val {name}: {value_type}{'' if required else '?'}" + ("" if required else " = null")


def csharp_declaration(definition, definitions, derived):
    model = definition["x-dotkt-model"]
    init_fields = model.get("csharpInit", [])
    all_fields = dict(fields(definition, "csharp"))
    ordered = model.get("csharpParameters", [name for name in all_fields if name not in init_fields])
    mapped = {entry for entry in ordered if isinstance(entry, str)} | set(init_fields) | set(model.get("csharpProjected", []))
    if mapped != all_fields.keys():
        raise ValueError(f'{model["name"]}: model properties must cover the complete schema: {mapped ^ all_fields.keys()}')
    parameters = []
    for entry in ordered:
        if isinstance(entry, str):
            parameters.append(parameter(entry, all_fields[entry], definition, definitions, "csharp"))
        else:
            parameters.append(f'{entry["type"]} {entry["name"]} = {entry["default"]}')
    declaration = f'    public sealed partial record {model["name"]}'
    if parameters:
        declaration += "(" + ", ".join(parameters) + ")"
    if derived:
        declaration += " : TypeNode"
    if not init_fields:
        return declaration + ";"
    lines = [declaration, "    {"]
    for name in init_fields:
        field = all_fields[name]
        value_type = model_type(field, definitions, "csharp")
        display = field.get("x-dotkt-csharp-name", name[0].upper() + name[1:])
        optional = name not in definition.get("required", [])
        lines.append(f"        public {value_type}{'?' if optional else ''} {display} {{ get; init; }}")
    return "\n".join([*lines, "    }"])


def kotlin_declaration(definition, definitions, derived):
    model = definition["x-dotkt-model"]
    parameters = [parameter(name, field, definition, definitions, "kotlin")
                  for name, field in fields(definition, "kotlin")]
    if not parameters:
        declaration = f'    data object {model["name"]}'
    else:
        declaration = f'    data class {model["name"]}(' + ", ".join(parameters) + ")"
    return declaration + (" : TypeNode()" if derived else "")


def wire_fields(definition, language):
    """Required first, then the model's explicit optional-field order."""
    available = dict(fields(definition, language))
    preferred = definition["x-dotkt-model"].get(f"{language}Parameters", [])
    order = [key for key in preferred if isinstance(key, str) and key in available]
    order += [key for key in available if key not in order]
    required = definition.get("required", [])
    return [(key, available[key]) for key in order if key in required] + [
        (key, available[key]) for key in order if key not in required]


def csharp_read_value(field, definitions):
    if "$ref" in field:
        name = model_type(field, definitions, "csharp")
        method = "ReadJson" if name == "TypeNode" else f"Read{name}Json"
        return f"{method}(ref reader)"
    if field.get("type") == "array":
        element = field["items"]
        return "ReadTypeArrayJson(ref reader)" if "$ref" in element else "ReadStringArrayJson(ref reader)"
    return {"string": "reader.GetString()", "integer": "reader.GetInt32()", "boolean": "reader.GetBoolean()"}[
        field.get("type", "string")]


def csharp_constraints(field, expression):
    checks = []
    if "enum" in field:
        choices = " or ".join(json.dumps(value) for value in field["enum"])
        checks.append(f"{expression} is not null && {expression} is not ({choices})")
    if "minimum" in field:
        checks.append(f'{expression} < {field["minimum"]}')
    if "maximum" in field:
        checks.append(f'{expression} > {field["maximum"]}')
    return checks


def csharp_reader(definition, definitions):
    model = definition["x-dotkt-model"]
    name = model["name"]
    all_fields = fields(definition, "csharp")
    lines = [f"    private static {name} Read{name}Json(ref Utf8JsonReader reader)", "    {",
             "        RequireToken(reader, JsonTokenType.StartObject);"]
    for key, field in all_fields:
        lines.append(f"        {model_type(field, definitions, 'csharp')}? value_{key} = null;")
    lines += ["        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)", "        {",
              "            RequireToken(reader, JsonTokenType.PropertyName);",
              "            var property = reader.GetString();", "            if (!reader.Read()) throw new FormatException();",
              "            switch (property)", "            {"]
    for key, field in all_fields:
        lines += [f'                case "{key}":', f"                    value_{key} = {csharp_read_value(field, definitions)};"]
        for check in csharp_constraints(field, f"value_{key}"):
            lines.append(f"                    if ({check}) throw new FormatException();")
        lines += ["                    break;"]
    # Unknown fields are left to the document schema gate, as for the existing DOM adapters.
    lines += ["                default: reader.Skip(); break;", "            }", "        }",
              "        RequireToken(reader, JsonTokenType.EndObject);"]
    arguments = {key: f"value_{key}" + (" ?? throw new FormatException()" if key in definition.get("required", []) else "")
                 for key, field in all_fields}
    if "csharpReadFactory" in model:
        expression = model["csharpReadFactory"].format_map(arguments)
    else:
        init = model.get("csharpInit", [])
        order = model.get("csharpParameters", [key for key, field in all_fields if key not in init])
        expression = f"new {name}(" + ", ".join(arguments[key] for key in order) + ")"
        if init:
            expression += " { " + ", ".join(f"{key[0].upper() + key[1:]} = {arguments[key]}" for key in init) + " }"
    lines += [f"        return {expression};", "    }", ""]
    return lines


def csharp_write_value(field, expression, definitions, indent):
    if "$ref" in field:
        name = model_type(field, definitions, "csharp")
        method = "WriteJson" if name == "TypeNode" else f"Write{name}Json"
        return [f"{indent}{method}(writer, {expression});"]
    if field.get("type") == "array":
        return [f"{indent}writer.WriteStartArray();", f"{indent}foreach (var item in {expression})", f"{indent}{{",
                *csharp_write_value(field["items"], "item", definitions, indent + "    "),
                f"{indent}}}", f"{indent}writer.WriteEndArray();"]
    method = {"string": "WriteStringValue", "integer": "WriteNumberValue", "boolean": "WriteBooleanValue"}[
        field.get("type", "string")]
    return [f"{indent}writer.{method}({expression});"]


def csharp_writer_body(definition, definitions, derived, indent):
    model = definition["x-dotkt-model"]
    lines = []
    if "csharpWriteValidation" in model:
        lines.append(f'{indent}{model["csharpWriteValidation"]};')
    lines.append(f"{indent}writer.WriteStartObject();")
    if derived:
        lines.append(f'{indent}writer.WriteString("t", "{definition["properties"]["t"]["const"]}");')
    for key, field in wire_fields(definition, "csharp"):
        member = field.get("x-dotkt-csharp-name", key[0].upper() + key[1:])
        expression = field.get("x-dotkt-csharp-wire-value", f"node.{member}")
        optional = key not in definition.get("required", [])
        field_indent = indent
        if optional:
            condition = "{ Length: > 0 }" if field.get("x-dotkt-omit-empty") else "{ }"
            lines += [f"{indent}if (({expression}) is {condition} value_{key})", f"{indent}{{"]
            expression = f"value_{key}"
            field_indent += "    "
        for check in csharp_constraints(field, expression):
            lines.append(f"{field_indent}if ({check}) throw new ArgumentException();")
        lines += [f'{field_indent}writer.WritePropertyName("{key}");',
                  *csharp_write_value(field, expression, definitions, field_indent)]
        if optional:
            lines.append(f"{indent}}}")
    return [*lines, f"{indent}writer.WriteEndObject();"]


def csharp_json(variants, values, definitions):
    lines = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "#nullable enable", "using System;", "using System.Collections.Generic;", "using System.Text.Json;",
             "namespace DotKt.Bir;", "", "public abstract partial record TypeNode", "{",
             "    // Boundary-only streaming codec: no JSON DOM or generic property bag.",
             "    public static TypeNode ReadJson(ref Utf8JsonReader reader)", "    {",
             "        RequireToken(reader, JsonTokenType.StartObject);", "        var probe = reader;",
             "        while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)", "        {",
             "            RequireToken(probe, JsonTokenType.PropertyName);", "            var property = probe.GetString();",
             "            if (!probe.Read()) throw new FormatException();", '            if (property == "t")',
             "                return probe.GetString() switch", "                {"]
    for definition in variants:
        lines.append(f'                    "{definition["properties"]["t"]["const"]}" => Read{definition["x-dotkt-model"]["name"]}Json(ref reader),')
    lines += ["                    _ => throw new FormatException(),", "                };", "            probe.Skip();",
              "        }", "        throw new FormatException();", "    }", "",
              "    public static void WriteJson(Utf8JsonWriter writer, TypeNode type)", "    {", "        switch (type)", "        {"]
    for definition in variants:
        lines += [f'            case {definition["x-dotkt-model"]["name"]} node:', "            {",
                  *csharp_writer_body(definition, definitions, True, "                "),
                  "                break;", "            }"]
    lines += ["            default: throw new ArgumentException();", "        }", "    }", ""]
    for definition in [*variants, *values]:
        lines += csharp_reader(definition, definitions)
    for definition in values:
        name = definition["x-dotkt-model"]["name"]
        lines += [f"    private static void Write{name}Json(Utf8JsonWriter writer, {name} node)", "    {",
                  *csharp_writer_body(definition, definitions, False, "        "), "    }", ""]
    for name, element_type, read in [("Type", "TypeNode", "ReadJson(ref reader)"), ("String", "string", "reader.GetString()!")]:
        lines += [f"    private static {element_type}[] Read{name}ArrayJson(ref Utf8JsonReader reader)", "    {",
                  "        RequireToken(reader, JsonTokenType.StartArray);", f"        var items = new List<{element_type}>();",
                  "        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)", f"            items.Add({read});",
                  "        RequireToken(reader, JsonTokenType.EndArray);", "        return items.ToArray();", "    }", ""]
    lines += ["    private static void RequireToken(Utf8JsonReader reader, JsonTokenType token)", "    {",
              "        if (reader.TokenType != token) throw new FormatException();", "    }", "}", ""]
    return "\n".join(lines)


def kotlin_write_value(field, expression, indent, definitions):
    if "$ref" in field:
        name = model_type(field, definitions, "kotlin")
        method = "write" if name == "TypeNode" else f"write{name}"
        return [f"{indent}{method}({expression}, sb)"]
    if field.get("type") == "array":
        return [f"{indent}sb.append('[')", f"{indent}{expression}.forEachIndexed {{ index, item ->",
                f"{indent}    if (index > 0) sb.append(',')",
                *kotlin_write_value(field["items"], "item", indent + "    ", definitions),
                f"{indent}}}", f"{indent}sb.append(']')"]
    if field.get("type", "string") == "string":
        return [f"{indent}writeString({expression}, sb)"]
    return [f"{indent}sb.append({expression})"]


def kotlin_writer_body(definition, derived, indent, definitions):
    prefix = '{"t":"' + definition["properties"]["t"]["const"] + '"' if derived else '{'
    lines = [f"{indent}sb.append({json.dumps(prefix)})"]
    for index, (key, field) in enumerate(wire_fields(definition, "kotlin")):
        field_indent = indent
        optional = key not in definition.get("required", [])
        if optional:
            lines += [f"{indent}if ({key} != null) {{"]
            field_indent += "    "
        prefix = ',' if derived or index > 0 else ''
        lines += [f"{field_indent}sb.append({json.dumps(prefix + json.dumps(key) + ':')})",
                  *kotlin_write_value(field, key, field_indent, definitions)]
        if optional:
            lines.append(f"{indent}}}")
    return [*lines, f"{indent}sb.append('}}')"]


def kotlin_json(variants, values, definitions):
    lines = ["// Generated from docs/bir-cir.schema.json by scripts/generate-ir-model.py.",
             "package kotc.bir", "", "internal object TypeNodeJson {",
             "    fun encode(type: TypeNode): String = buildString { write(type, this) }", "",
             "    private fun write(value: TypeNode, sb: StringBuilder) {", "        when (value) {"]
    for definition in variants:
        model = definition["x-dotkt-model"]
        if not model.get("kotlin", True):
            continue
        match = "is " if fields(definition, "kotlin") else ""
        lines += [f'            {match}TypeNode.{model["name"]} -> with(value) {{',
                  *kotlin_writer_body(definition, True, "                ", definitions), "            }"]
    lines += ["        }", "    }", ""]
    for definition in values:
        name = definition["x-dotkt-model"]["name"]
        lines += [f"    private fun write{name}(value: TypeNode.{name}, sb: StringBuilder) = with(value) {{",
                  *kotlin_writer_body(definition, False, "        ", definitions), "    }", ""]
    lines += [r'''    private fun writeString(value: String, sb: StringBuilder) {
        sb.append('"')
        for (char in value) {
            when (char) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (char.code < 0x20) sb.append("\\u").append(char.code.toString(16).padStart(4, '0'))
                        else sb.append(char)
            }
        }
        sb.append('"')
    }''', "}", ""]
    return "\n".join(lines)


def outputs(schema):
    definitions = schema["$defs"]
    variants = [definitions[item["$ref"].removeprefix("#/$defs/")] for item in definitions["type"]["oneOf"]]
    # Source classifier paths are values carried by an FQN, not an additional TypeNode variant.
    values = [definitions["sourceClassifier"]]
    csharp = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
              "#nullable enable", "namespace DotKt.Bir;", "", "public abstract partial record TypeNode", "{"]
    for definition in variants:
        csharp += [csharp_declaration(definition, definitions, derived=True), ""]
    csharp += [csharp_declaration(definition, definitions, derived=False) for definition in values]
    csharp += ["}", "", "// Test contract metadata, not an IR object/property bag.",
               "internal static class TypeNodeWireContract", "{",
               "    internal static readonly (string Tag, string[] Fields)[] Properties =", "    ["]
    for definition in variants:
        tag = definition["properties"]["t"]["const"]
        keys = ", ".join(json.dumps(key) for key in definition["properties"])
        csharp.append(f'        ("{tag}", [{keys}]),')
    for definition in values:
        name = definition["x-dotkt-model"]["name"]
        keys = ", ".join(json.dumps(key) for key in definition["properties"])
        csharp.append(f'        ("{name}", [{keys}]),')
    csharp += ["    ];", "}", ""]
    kotlin = ["// Generated from docs/bir-cir.schema.json by scripts/generate-ir-model.py.",
              "// Semantic helpers and JSON boundary encoding are maintained separately.",
              "package kotc.bir", "", "sealed class TypeNode {"]
    for definition in variants:
        if definition["x-dotkt-model"].get("kotlin", True):
            kotlin += [kotlin_declaration(definition, definitions, derived=True), ""]
    kotlin += [kotlin_declaration(definition, definitions, derived=False) for definition in values]
    kotlin += ["", "    fun toJson(): String = TypeNodeJson.encode(this)", "}", ""]
    return {
        ROOT / "toolchain/bir-common/TypeNode.Model.Generated.cs": "\n".join(csharp),
        ROOT / "toolchain/bir-common/TypeNode.Json.Generated.cs": csharp_json(variants, values, definitions),
        ROOT / "toolchain/kotc/src/main/kotlin/kotc/bir/TypeNode.kt": "\n".join(kotlin),
        ROOT / "toolchain/kotc/src/main/kotlin/kotc/bir/TypeNodeJson.kt": kotlin_json(variants, values, definitions),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="check generated files without modifying them")
    args = parser.parse_args()
    expected = outputs(json.loads(SCHEMA.read_text(encoding="utf-8")))
    stale = []
    for path, content in expected.items():
        if not path.exists() or path.read_text(encoding="utf-8") != content:
            stale.append(str(path.relative_to(ROOT)))
            if not args.check:
                path.write_text(content, encoding="utf-8", newline="\n")
    if args.check and stale:
        raise SystemExit("Generated IR models are stale: " + ", ".join(stale))
    print(f"IR models {'checked' if args.check else 'generated'}: {len(expected)} files")


if __name__ == "__main__":
    main()
