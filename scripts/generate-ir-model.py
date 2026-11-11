#!/usr/bin/env python3
"""Generate structural IR models from the maintained logical schema.

Semantic operations, equality and representation choices stay in their owning
compiler layers. Structural BIR/CIR use bir-cir.schema.json; source declaration
metadata uses its referenced kotlin-typealias.schema.json. No generic
extension-data bag is emitted.
"""
import argparse
import json
from pathlib import Path
from source_metadata_model import outputs as source_metadata_outputs
from bir_literal_models import outputs as bir_literal_outputs
from cir_messagepack import generate as cir_messagepack

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "docs/bir-cir.schema.json"


def non_null_field(field):
    alternatives = field.get("oneOf", field.get("anyOf", []))
    if len(alternatives) == 2 and {"type": "null"} in alternatives:
        return next(item for item in alternatives if item != {"type": "null"})
    return field


def allows_null(field, definitions):
    if "$ref" in field:
        return allows_null(definitions[field["$ref"].removeprefix("#/$defs/")], definitions)
    if "type" in field:
        return field["type"] == "null" or isinstance(field["type"], list) and "null" in field["type"]
    if "anyOf" in field:
        return any(allows_null(item, definitions) for item in field["anyOf"])
    if "oneOf" in field:
        return sum(allows_null(item, definitions) for item in field["oneOf"]) == 1
    return field.get("const", False) is None or None in field.get("enum", [])


def model_type(field, definitions, language):
    if language == "csharp" and "x-dotkt-csharp-type" in field:
        return field["x-dotkt-csharp-type"]
    field = non_null_field(field)
    if "$ref" in field:
        name = field["$ref"].removeprefix("#/$defs/")
        if language == "csharp" and "x-dotkt-csharp-type" in definitions[name]:
            return definitions[name]["x-dotkt-csharp-type"]
        if name == "memberRef":
            return "MemberRefNode"
        return "TypeNode" if name == "type" else definitions[name]["x-dotkt-model"]["name"]
    kind = field.get("type", "string" if "enum" in field else
                     "boolean" if isinstance(field.get("const"), bool) else None)
    if kind == "array":
        element = model_type(field["items"], definitions, language)
        return element + "[]" if language == "csharp" else f"List<{element}>"
    return {"csharp": {"string": "string", "integer": "int", "boolean": "bool"},
            "kotlin": {"string": "String", "integer": "Int", "boolean": "Boolean"}}[language][kind]


def fields(definition, language):
    return [(name, field) for name, field in definition["properties"].items()
            if name != "t" and field.get(f"x-dotkt-{language}", True)]


def type_parameter_models(definitions):
    """Semantic declaration facts; physical-only extensions have no Kotlin producer."""
    definition = definitions["typeParam"]
    name = definition["x-dotkt-model"]["name"]
    header = "// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py."
    cs_fields = fields(definition, "csharp")
    kt_fields = fields(definition, "kotlin")
    cs = [header, "#nullable enable", "namespace DotKt.Bir;", "",
          f'public sealed partial record {name}(' + ", ".join(parameter(key, shape, definition, definitions, "csharp")
                                                       for key, shape in cs_fields) + ");", ""]
    # The declaration now owns its semantic fields. This adapter remains only at
    # the frontier to passes whose surrounding declaration graph is still DOM.
    dom = [header, "#nullable enable", "using System;", "using System.Linq;",
           "using System.Text.Json.Nodes;", "namespace DotKt.Bir;", "",
           "// Temporary boundary to not-yet-migrated enclosing declarations.",
           f"public sealed partial record {name}", "{",
           f"    public static {name} Read(JsonNode node)", "    {",
           "        var source = (JsonObject)node;",
           "        foreach (var field in source)", "            switch (field.Key)", "            {"]
    dom += [f'                case "{key}": break;' for key, _ in cs_fields]
    dom += ["                default: throw new FormatException();", "            }",
            f"        return new {name}("]
    read_fields = []
    for key, shape in cs_fields:
        value = f'source["{key}"]!'
        if shape.get("type") == "array":
            item = "TypeNode.Read(item!) ?? throw new FormatException()" if "$ref" in shape["items"] else "item!.GetValue<string>()"
            read = f"((JsonArray){value}).Select(item => {item}).ToArray()"
        else:
            read = f'{value}.GetValue<{model_type(shape, definitions, "csharp")}>()'
        if key not in definition["required"]:
            read = f'source.ContainsKey("{key}") ? {read} : null'
        read_fields.append("            " + read)
    dom += [",\n".join(read_fields) + ");", "    }", "",
            "    public JsonObject ToDom()", "    {", "        var result = new JsonObject();"]
    for key, shape in cs_fields:
        value = key[0].upper() + key[1:]
        optional = key not in definition["required"]
        indent = "            " if optional else "        "
        if optional:
            dom += [f"        if ({value} is {{ }} {key})", "        {"]
            value = key
        if shape.get("type") == "array":
            item = "TypeNode.Write(item)" if "$ref" in shape["items"] else "(JsonNode)JsonValue.Create(item)!"
            write = f"new JsonArray({value}.Select(item => {item}).ToArray())"
        else:
            write = value
        dom += [f'{indent}result["{key}"] = {write};']
        if optional:
            dom += ["        }"]
    dom += ["        return result;", "    }", "}", ""]
    kt = [header, "package kotc.bir", "", f"data class {name}(",
          ",\n".join("    " + parameter(key, shape, definition, definitions, "kotlin") for key, shape in kt_fields),
          ") {", "    fun toJson(): String = buildString {",
          "        append('{')"]
    for index, (key, shape) in enumerate(kt_fields):
        optional = key not in definition["required"]
        prefix = ("," if index else "") + json.dumps(key) + ":"
        indent = "            " if optional else "        "
        value = "child" if optional else key
        if optional:
            kt += [f"        {key}?.let {{ child ->"]
        kt += [f"{indent}append({json.dumps(prefix)})"]
        if shape.get("type") == "array":
            kt += [f"{indent}append('[')", f"{indent}{value}.forEachIndexed {{ index, item ->",
                   f"{indent}    if (index > 0) append(',')"]
            item = "append(item.toJson())" if "$ref" in shape["items"] else "TypeNodeJson.writeString(item, this)"
            kt += [f"{indent}    {item}", f"{indent}}}", f"{indent}append(']')"]
        elif model_type(shape, definitions, "kotlin") == "String":
            kt += [f"{indent}TypeNodeJson.writeString({value}, this)"]
        else:
            kt += [f"{indent}append({value})"]
        if optional:
            kt += ["        }"]
    kt += ["        append('}')", "    }", "}", ""]
    return {
        ROOT / "toolchain/bir-common/TypeParameterDeclaration.Model.Generated.cs": "\n".join(cs),
        ROOT / "toolchain/bir-common/TypeParameterDeclaration.Dom.Generated.cs": "\n".join(dom),
        ROOT / "toolchain/kotc/src/main/kotlin/kotc/bir/TypeParameterDeclaration.kt": "\n".join(kt),
    }


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


def csharp_constraints(field, expression, nullable=True):
    checks = []
    if "enum" in field:
        choices = " or ".join(json.dumps(value) for value in field["enum"])
        predicate = f"{expression} is not ({choices})"
        checks.append(f"{expression} is not null && {predicate}" if nullable else predicate)
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
        for check in csharp_constraints(field, expression, nullable=False):
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
    lines += [r'''    internal fun writeString(value: String, sb: StringBuilder) {
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


def metadata_models(definitions):
    root = definitions["cirAttributeApplication"]
    named = definitions["cirNamedAttributeArgument"]
    union = definitions["cirAttributeArgument"]
    variants = [definitions[item["$ref"].removeprefix("#/$defs/")] for item in union["oneOf"]]
    scalar_names = {definitions[item["$ref"].removeprefix("#/$defs/")]["x-dotkt-model"]["name"]
                    for item in definitions["cirScalarValue"]["oneOf"]}
    lines = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "#nullable enable", "using DotKt.Bir;", "using System.Text.Json.Serialization;",
             "namespace DotKt.Cir;", ""]

    def declaration(definition, base=None, indent=""):
        properties = [(key, field) for key, field in definition["properties"].items() if key != "a"]
        required = definition.get("required", [])
        properties = [pair for pair in properties if pair[0] in required] + [
            pair for pair in properties if pair[0] not in required]
        parameters = []
        for key, field in properties:
            annotations = [f'JsonPropertyName("{key}")']
            if key not in required:
                annotations.append("JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)")
            if field.get("$ref") == "#/$defs/type":
                annotations.append("JsonConverter(typeof(TypeNodeMetadataConverter))")
            parameters.append("[property: " + ", ".join(annotations) + "] " +
                              parameter(key, field, definition, definitions, "csharp"))
        result = indent + "public sealed partial record " + definition["x-dotkt-model"]["name"]
        result += "(" + ", ".join(parameters) + ")"
        bases = [base] if base else []
        if base and definition["x-dotkt-model"]["name"] in scalar_names:
            bases.append("IScalarValue")
        return result + (" : " + ", ".join(bases) if bases else "") + ";"

    lines += ["[JsonConverter(typeof(AttributeApplicationJsonConverter))]", declaration(root), "", declaration(named), "",
              '[JsonPolymorphic(TypeDiscriminatorPropertyName = "a")]']
    for variant in variants:
        lines.append(f'[JsonDerivedType(typeof(AttributeArgument.{variant["x-dotkt-model"]["name"]}), "{variant["properties"]["a"]["const"]}")]')
    lines += ["public abstract partial record AttributeArgument", "{"]
    lines += [declaration(variant, "AttributeArgument", "    ") for variant in variants]
    lines += ["}", "", "// Test-only field inventory from the same schema.", "internal static class AttributeWireContract", "{",
              "    internal static readonly (string Tag, string[] Fields)[] Properties =", "    ["]
    for definition in [root, named, *variants]:
        tag = definition["properties"]["a"]["const"] if "a" in definition["properties"] else definition["x-dotkt-model"]["name"]
        keys = ", ".join(json.dumps(key) for key in definition["properties"])
        lines.append(f'        ("{tag}", [{keys}]),')
    return "\n".join([*lines, "    ];", "}", ""])


def declaration_models(definitions):
    """Concrete physical declarations; optional fields retain absent/null/value explicitly."""
    selected = {key: definition for key, definition in definitions.items()
                if isinstance(definition, dict) and definition.get("x-dotkt-model", {}).get("domain") == "cirDeclarations"}
    header = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
              "#nullable enable", "using DotKt.Bir;", "using System.Text.Json.Serialization;", "namespace DotKt.Cir;", ""]
    model = header.copy()
    projection = ["// <auto-generated />", "// Temporary final bir2cir declaration boundary; removed with typed owning passes.",
                  "#nullable enable", "using System.Text.Json.Nodes;", "using DotKt.Bir;", "using DotKt.Cir;", "",
                  "static partial class CirDeclarationProjection", "{"]

    def read(field, value):
        field = non_null_field(field)
        if "$ref" in field:
            key = field["$ref"].removeprefix("#/$defs/")
            if key == "type":
                return f"TypeJson.Read({value})"
            if key == "memberRef":
                return f"MemberRefNode.Read({value})"
            if key in ("cirAttributeArgument", "cirAttributeApplication"):
                return f"((JsonValue){value}).GetValue<{model_type(field, definitions, 'csharp')}>()"
            if key in ("cirStatementList", "cirExpressionList"):
                method = "ReadStatementNode" if key == "cirStatementList" else "ReadExpressionNode"
                return f"((JsonArray){value}).Select(item => {method}(item!)).ToArray()"
            if key == "cirExpression":
                return f"ReadExpressionNode({value})"
            return f"Read{model_type(field, definitions, 'csharp')}({value})"
        if field.get("type") == "array":
            return f"((JsonArray){value}).Select(item => {read(field['items'], 'item!')}).ToArray()"
        if field.get("x-dotkt-csharp-type") == "byte[]":
            return f"Convert.FromBase64String({value}.GetValue<string>())"
        if field.get("type") == "object" and "additionalProperties" in field:
            return f"((JsonObject){value}).ToDictionary(item => item.Key, item => {read(field['additionalProperties'], 'item.Value!')}, StringComparer.Ordinal)"
        return f"{value}.GetValue<{model_type(field, definitions, 'csharp')}>()"

    for key, definition in selected.items():
        name = definition["x-dotkt-model"]["name"]
        if "enum" in definition:
            model += [f"[JsonConverter(typeof(JsonStringEnumConverter<{name}>))]", f"public enum {name}", "{"]
            projection += [f"    static {name} Read{name}(JsonNode node) => node.GetValue<string>() switch", "    {"]
            for item in definition["enum"]:
                member = item[0].upper() + item[1:]
                model += [f'    [JsonStringEnumMemberName("{item}")] {member},']
                projection += [f'        "{item}" => {name}.{member},']
            model += ["}", ""]
            projection += ['        _ => throw new InvalidOperationException("Malformed CIR declaration kind"),', "    };", ""]
            continue
        interfaces = definition["x-dotkt-model"].get("interfaces", [])
        model += [f"public sealed partial record {name}" + (" : " + ", ".join(interfaces) if interfaces else ""), "{"]
        properties = definition["properties"]
        required = definition.get("required", [])
        projection += [f"    public static {name} Read{name}(JsonNode node)", "    {"]
        keys = ", ".join(json.dumps(field) for field in properties)
        projection += [f"        CheckFields(node, [{keys}]);", f"        return new {name}", "        {"]
        for field_name, field in properties.items():
            cs_name = field_name[0].upper() + field_name[1:]
            value_type = model_type(field, definitions, "csharp")
            annotations = [f'JsonPropertyName("{field_name}")']
            if field_name in required:
                annotations.append("JsonRequired")
                declaration = f"required {value_type}"
                expression = read(field, f'node["{field_name}"]!')
                source_defaults = definition["x-dotkt-model"].get("sourceDefaults", {})
                if field_name in source_defaults:
                    default_value = source_defaults[field_name]
                    literal = "[]" if default_value == [] else json.dumps(default_value)
                    expression = f'node.AsObject().ContainsKey("{field_name}") ? {expression} : {literal}'
            else:
                annotations.append("JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)")
                if not allows_null(field, definitions):
                    annotations.append("JsonConverter(typeof(NonNullOptionalFieldConverterFactory))")
                declaration = f"OptionalField<{value_type}>"
                if name == "CirField" and field_name == "constant":
                    expression = ('node.AsObject().ContainsKey("constant") ? '
                                  'OptionalField<AttributeArgument>.Of(AttributeProjection.Literal(node["constant"], TypeJson.Read(node["type"]))) : default')
                else:
                    null_option = "" if allows_null(field, definitions) else ", allowNull: false"
                    expression = f'ReadOptional(node, "{field_name}", value => {read(field, "value")}{null_option})'
            model += ["    [" + ", ".join(annotations) + "]", f"    public {declaration} {cs_name} {{ get; init; }}"]
            projection += [f"            {cs_name} = {expression},"]
        model += ["}", ""]
        projection += ["        };", "    }", ""]
    model += ["// Test-only schema inventory, not an IR field bag.", "internal static class DeclarationWireContract", "{",
              "    internal static readonly (string Name, string[] Fields)[] Properties =", "    ["]
    for definition in selected.values():
        if "properties" not in definition:
            continue
        keys = ", ".join(json.dumps(key) for key in definition["properties"])
        model += [f'        ("{definition["x-dotkt-model"]["name"]}", [{keys}]),']
    model += ["    ];", "    internal static readonly (string Name, string[] Fields)[] NullableProperties =", "    ["]
    for definition in selected.values():
        if "properties" not in definition:
            continue
        keys = ", ".join(json.dumps(key) for key, field in definition["properties"].items()
                         if allows_null(field, definitions))
        model += [f'        ("{definition["x-dotkt-model"]["name"]}", [{keys}]),']
    return "\n".join([*model, "    ];", "}", ""]), "\n".join([*projection, "}", ""])


def executable_models(definitions):
    """Physical node variants and their executable-only child edges share one schema."""
    nodes = {key: definition for key, definition in definitions.items()
             if isinstance(definition, dict) and definition.get("x-dotkt-model", {}).get("domain") == "cirExecutable"}
    values = {key: definition for key, definition in definitions.items()
              if isinstance(definition, dict) and definition.get("x-dotkt-model", {}).get("domain") == "cirExecutableValues"}
    selected = {**values, **nodes}
    union_keys = [item["$ref"].removeprefix("#/$defs/") for item in definitions["cirExecutable"]["oneOf"]]
    if len(union_keys) != len(nodes) or set(union_keys) != nodes.keys():
        raise ValueError("CIR executable union must include exactly the concrete physical nodes")
    tags = [definition["properties"]["k"]["const"] for definition in nodes.values()]
    if len(set(tags)) != len(tags):
        raise ValueError("CIR executable tags must be unique")
    for role in ("statement", "expression"):
        role_keys = [item["$ref"].removeprefix("#/$defs/")
                     for item in definitions["cir" + role.capitalize() + "Node"]["oneOf"]]
        expected = {key for key, definition in nodes.items() if role in definition["x-dotkt-model"]["roles"]}
        if len(role_keys) != len(expected) or set(role_keys) != expected:
            raise ValueError(f"CIR {role} union and node roles disagree")
    if any(not definition["x-dotkt-model"]["roles"] or
           set(definition["x-dotkt-model"]["roles"]) - {"statement", "expression"} for definition in nodes.values()):
        raise ValueError("Every CIR executable node must declare its physical roles")
    model = ["// <auto-generated />", "// Source: docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
             "#nullable enable", "using System.Collections.Generic;", "using DotKt.Bir;",
             "using System.Text.Json.Serialization;", "namespace DotKt.Cir;", "",
             '[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]']
    projection = ["// <auto-generated />", "// Temporary final bir2cir boundary; removed with typed owning passes.",
                  "#nullable enable", "using System.Text.Json.Nodes;", "using DotKt.Bir;", "using DotKt.Cir;", "",
                  "static partial class CirDeclarationProjection", "{"]
    for definition in nodes.values():
        name = definition["x-dotkt-model"]["name"]
        tag = definition["properties"]["k"]["const"]
        model += [f'[JsonDerivedType(typeof({name}), "{tag}")]']
    model += ["public abstract partial record CirNode : ICirNode", "{",
              "    [JsonIgnore] public abstract string Tag { get; }",
              "    [JsonIgnore] public abstract IEnumerable<ICirNode> Children { get; }", "}", ""]

    def child_shape(field):
        field = non_null_field(field)
        if "$ref" in field:
            key = field["$ref"].removeprefix("#/$defs/")
            if key in ("cirStatementNode", "cirExpressionNode"):
                return ("node",)
            if key in values and any(child_shape(value) for value in values[key]["properties"].values()):
                return ("container",)
        if field.get("type") == "array" and (element := child_shape(field["items"])):
            return ("array", element)
        return None

    def children(shape, value, indent, depth=0):
        if shape[0] == "node":
            return [f"{indent}yield return {value};"]
        if shape[0] == "container":
            return [f"{indent}foreach (var child{depth} in {value}.Children) yield return child{depth};"]
        item = f"item{depth}"
        return [f"{indent}foreach (var {item} in {value})", f"{indent}{{",
                *children(shape[1], item, indent + "    ", depth + 1), f"{indent}}}"]

    def read(field, value):
        field = non_null_field(field)
        if "$ref" in field:
            key = field["$ref"].removeprefix("#/$defs/")
            if key == "type":
                return f"TypeJson.Read({value})"
            if key == "memberRef":
                return f"MemberRefNode.Read({value})"
            if key == "cirStatementNode":
                return f"ReadStatementNode({value})"
            if key == "cirExpressionNode":
                return f"ReadExpressionNode({value})"
            return f"Read{model_type(field, definitions, 'csharp')}({value})"
        if field.get("type") == "array":
            return f"((JsonArray){value}).Select(item => {read(field['items'], 'item!')}).ToArray()"
        return f"{value}.GetValue<{model_type(field, definitions, 'csharp')}>()"

    projection += ["    static ICirStatement ReadStatementNode(JsonNode node) => ReadCirNode(node) as ICirStatement ?? throw new FormatException();",
                   "    static ICirExpression ReadExpressionNode(JsonNode node) => ReadCirNode(node) as ICirExpression ?? throw new FormatException();",
                   '    static CirNode ReadCirNode(JsonNode node) => node["k"]!.GetValue<string>() switch', "    {"]
    for definition in nodes.values():
        name = definition["x-dotkt-model"]["name"]
        tag = definition["properties"]["k"]["const"]
        projection += [f'        "{tag}" => Read{name}(node),']
    projection += ["        _ => throw new FormatException(),", "    };", ""]
    for key, definition in selected.items():
        info = definition["x-dotkt-model"]
        name = info["name"]
        is_node = key in nodes
        bases = (["CirNode"] + ["ICir" + role.capitalize() for role in info["roles"]]) if is_node else ["ICirChildren"]
        model += [f'public sealed partial record {name} : ' + ", ".join(bases), "{"]
        if is_node:
            tag = definition["properties"]["k"]["const"]
            model += [f'    [JsonIgnore] public override string Tag => "{tag}";']
        required = definition.get("required", [])
        projection += [f"    static {name} Read{name}(JsonNode node)", "    {"]
        keys = ", ".join(json.dumps(field) for field in [*definition["properties"], *info.get("sourceConsumed", [])])
        projection += [f"        CheckFields(node, [{keys}]);"]
        if "sty" in info.get("sourceConsumed", []):
            projection += ["        CheckSourceStamp(node);"]
        if name == "CirConstant":
            projection += ['        var physicalType = TypeJson.Read(node["type"]!);']
        projection += [f"        return new {name}", "        {"]
        fields_to_walk = []
        for field_name, field in definition["properties"].items():
            if field_name == "k":
                continue
            cs_name = field_name[0].upper() + field_name[1:]
            value_type = model_type(field, definitions, "csharp")
            annotations = [f'JsonPropertyName("{field_name}")']
            if field_name in required:
                annotations += ["JsonRequired"]
                declaration = "required " + value_type
                expression = read(field, f'node["{field_name}"]!')
                if name == "CirConstant" and field_name == "type":
                    expression = "physicalType"
                if name == "CirConstant" and field_name == "value":
                    expression = ('node.AsObject().ContainsKey("value") ? '
                                  '(IScalarValue)AttributeProjection.Literal(node["value"], physicalType) '
                                  ': throw new FormatException()')
            else:
                annotations += ["JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)"]
                if not allows_null(field, definitions):
                    annotations += ["JsonConverter(typeof(NonNullOptionalFieldConverterFactory))"]
                declaration = f"OptionalField<{value_type}>"
                null_option = "" if allows_null(field, definitions) else ", allowNull: false"
                expression = f'ReadOptional(node, "{field_name}", value => {read(field, "value")}{null_option})'
            model += ["    [" + ", ".join(annotations) + "]", f"    public {declaration} {cs_name} {{ get; init; }}"]
            projection += [f"            {cs_name} = {expression},"]
            if shape := child_shape(field):
                fields_to_walk += [(cs_name, shape, field_name not in required)]
        model += ["    [JsonIgnore] public " + ("override " if is_node else "") + "IEnumerable<ICirNode> Children", "    {", "        get", "        {"]
        for field_name, shape, optional in fields_to_walk:
            if optional:
                model += [f"            if ({field_name}.HasValue)", "            {"]
                model += children(shape, field_name + ".Value", "                ")
                model += ["            }"]
            else:
                model += children(shape, field_name, "            ")
        model += ["            yield break;", "        }", "    }", "}", ""]
        projection += ["        };", "    }", ""]
    return "\n".join(model), "\n".join([*projection, "}", ""])


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
              "// Structural model; the boundary codec is generated in TypeNodeJson.kt.",
              "package kotc.bir", "", "sealed class TypeNode {"]
    for definition in variants:
        if definition["x-dotkt-model"].get("kotlin", True):
            kotlin += [kotlin_declaration(definition, definitions, derived=True), ""]
    kotlin += [kotlin_declaration(definition, definitions, derived=False) for definition in values]
    kotlin += ["", "    fun toJson(): String = TypeNodeJson.encode(this)", "}", ""]
    declaration_model, declaration_projection = declaration_models(definitions)
    executable_model, executable_projection = executable_models(definitions)
    return {
        ROOT / "toolchain/cir-common/MessagePack.Generated.cs": cir_messagepack(definitions, model_type, non_null_field, allows_null),
        **type_parameter_models(definitions),
        **bir_literal_outputs(ROOT, definitions),
        ROOT / "toolchain/cir-common/Executable.Model.Generated.cs": executable_model,
        ROOT / "toolchain/bir2cir/CirExecutableProjection.Generated.cs": executable_projection,
        ROOT / "toolchain/cir-common/Declaration.Model.Generated.cs": declaration_model,
        ROOT / "toolchain/bir2cir/CirDeclarationProjection.Generated.cs": declaration_projection,
        ROOT / "toolchain/cir-common/Attribute.Model.Generated.cs": metadata_models(definitions),
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
    expected.update(source_metadata_outputs(ROOT,
        json.loads((ROOT / "docs/kotlin-typealias.schema.json").read_text(encoding="utf-8"))))
    stale = []
    for path, content in expected.items():
        if not path.exists() or path.read_text(encoding="utf-8") != content:
            stale.append(str(path.relative_to(ROOT)))
            if not args.check:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8", newline="\n")
    if args.check and stale:
        raise SystemExit("Generated IR models are stale: " + ", ".join(stale))
    print(f"IR models {'checked' if args.check else 'generated'}: {len(expected)} files")


if __name__ == "__main__":
    main()
