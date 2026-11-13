"""Generate source declaration models from the existing alias schema, not CLR types."""
import json
from source_metadata_messagepack import outputs as messagepack_outputs


def outputs(root, schema):
    definitions = schema["$defs"]
    named = [schema, *(value for value in definitions.values() if "x-dotkt-model" in value)]

    def type_name(field):
        if "$ref" in field:
            target = definitions[field["$ref"].removeprefix("#/$defs/")]
            return target["x-dotkt-model"]["name"] if "x-dotkt-model" in target else type_name(target)
        if "x-dotkt-kotlin-type" in field:
            return field["x-dotkt-kotlin-type"]
        if field.get("type") == "array":
            return f'List<{type_name(field["items"])}>'
        return {"string": "String", "boolean": "Boolean", "integer": "Int"}[
            field.get("type", "string" if "enum" in field else None)]

    def variants(definition):
        info = definition["x-dotkt-model"]
        discriminator = info["discriminator"]
        for branch in definition.get("oneOf", [{}]):
            properties = {**definition["properties"], **branch.get("properties", {})}
            shape = properties[discriminator]
            for tag in shape.get("enum", [shape.get("const")]):
                if not isinstance(tag, str):
                    raise ValueError("A source union needs a closed string discriminator")
                fields = dict(properties)
                fields[discriminator] = {"const": tag}
                required = set(definition.get("required", [])) | set(branch.get("required", []))
                for condition in definition.get("allOf", []):
                    selected = condition.get("if", {}).get("properties", {}).get(discriminator)
                    if selected is None:
                        if "properties" in condition.get("if", {}):
                            raise ValueError("Unsupported source model discriminator condition")
                        continue  # Non-discriminator semantic relations remain the owning producer's facts.
                    if condition["if"] != {"properties": {discriminator: selected}} or set(selected) != {"const"}:
                        raise ValueError("Source model discriminator conditions require a single constant")
                    rule = condition.get("then" if selected.get("const") == tag else "else", {})
                    if set(rule) - {"required", "not"} or set(rule.get("not", {})) - {"required"}:
                        raise ValueError("Unsupported source model discriminator rule")
                    required.update(rule.get("required", []))
                    for forbidden in rule.get("not", {}).get("required", []):
                        fields.pop(forbidden)
                yield tag[0].upper() + tag[1:] + info["variantSuffix"], {
                    "properties": fields, "required": required,
                }

    def parameters(definition):
        properties = definition["properties"]
        return [(key, shape, key not in definition.get("required", []))
                for key, shape in properties.items() if "const" not in shape]

    header = "// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py."
    model = [header, "package kotc.bir", ""]
    writer = [header, "package kotc.bir", "", "internal object SourceMetadataJson {",
              "    fun encode(value: SourceTypeAlias): String = buildString { write(value, this) }",
              "    fun write(value: SourceTypeAlias, output: StringBuilder) = writeSourceTypeAlias(value, output)", ""]

    def declaration(name, definition, parent=None, indent=""):
        fields = [f'val {key}: {type_name(shape)}' + ("? = null" if optional else "")
                  for key, shape, optional in parameters(definition)]
        info = definition.get("x-dotkt-model", {})
        parents = ([parent] if parent else []) + info.get("interfaces", [])
        suffix = " : " + ", ".join(parents) if parents else ""
        if fields:
            model.append(f'{indent}data class {name}(\n' + ",\n".join(f"{indent}    {field}" for field in fields)
                         + f"\n{indent}){suffix}")
        else:
            model.append(f"{indent}data object {name}{suffix}")
        if "jsonWriter" in info:
            model[-1] += " {\n" + indent + "    override fun writeJson(output: StringBuilder) = " + info["jsonWriter"] + "(this, output)\n" + indent + "}"

    def write_value(shape, expression, indent):
        if shape.get("x-dotkt-codec") == "literalText":
            return [f"{indent}LiteralText.writeJson({expression}, sb)"]
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            if "x-dotkt-model" in target:
                return [f'{indent}write{target["x-dotkt-model"]["name"]}({expression}, sb)']
            return write_value(target, expression, indent)
        if shape.get("type") == "array":
            return [f"{indent}sb.append('[')", f"{indent}{expression}.forEachIndexed {{ index, item ->",
                    f"{indent}    if (index > 0) sb.append(',')",
                    *write_value(shape["items"], "item", indent + "    "), f"{indent}}}", f"{indent}sb.append(']')"]
        if "const" in shape:
            return [f"{indent}sb.append({json.dumps(json.dumps(shape['const']))})"]
        if type_name(shape) == "String":
            return [f"{indent}TypeNodeJson.writeString({expression}, sb)"]
        return [f"{indent}sb.append({expression})"]

    def write_object(definition, indent):
        lines = [f"{indent}sb.append('{{')"]
        # Both source record families start with at least one required field.
        ordered = sorted(definition["properties"].items(), key=lambda entry: entry[0] not in definition["required"])
        if not ordered or ordered[0][0] not in definition["required"]:
            raise ValueError("Source records need a required leading field")
        for index, (key, shape) in enumerate(ordered):
            optional = key not in definition["required"]
            nest = indent + "    " if optional else indent
            if optional:
                lines.append(f"{indent}value.{key}?.let {{ child ->")
            prefix = ("," if index else "") + json.dumps(key) + ":"
            lines.append(f"{nest}sb.append({json.dumps(prefix)})")
            lines.extend(write_value(shape, "child" if optional else f"value.{key}", nest))
            if optional:
                lines.append(f"{indent}}}")
        lines.append(f"{indent}sb.append('}}')")
        return lines

    for definition in named:
        info = definition["x-dotkt-model"]
        name = info["name"]
        if "discriminator" in info:
            model.append(f"sealed interface {name} {{")
            writer += [f"    private fun write{name}(value: {name}, sb: StringBuilder) {{", "        when (value) {"]
            for variant, shape in variants(definition):
                declaration(variant, shape, name, "    ")
                pattern = "is " if parameters(shape) else ""
                writer += [f"            {pattern}{name}.{variant} -> {{", *write_object(shape, "                "), "            }"]
            model += ["}", ""]
            writer += ["        }", "    }", ""]
        else:
            declaration(name, definition)
            model.append("")
            writer += [f"    private fun write{name}(value: {name}, sb: StringBuilder) {{",
                       *write_object(definition, "        "), "    }", ""]
    writer += ["}", ""]
    def cs_type(shape):
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            return target["x-dotkt-model"]["name"] if "x-dotkt-model" in target else cs_type(target)
        if "x-dotkt-csharp-type" in shape:
            return shape["x-dotkt-csharp-type"]
        if shape.get("type") == "array":
            return cs_type(shape["items"]) + "[]"
        return {"string": "string", "boolean": "bool", "integer": "int"}[
            shape.get("type", "string" if "enum" in shape else None)]

    def cs_name(key):
        return key[0].upper() + key[1:]

    def cs_parameter(key, shape, optional):
        return f'{cs_type(shape)}{"?" if optional else ""} {cs_name(key)}' + (" = null" if optional else "")

    cs_header = ["// <auto-generated />", "// Source: docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.",
                 "#nullable enable", "namespace DotKt.Bir;", ""]
    cs_model = list(cs_header)
    cs_codec = cs_header[:3] + ["using System.Text.Json;", "namespace DotKt.Bir;", "",
        "public static class SourceMetadataJson", "{",
        "    public static SourceTypeAlias Read(ref Utf8JsonReader reader) => ReadSourceTypeAlias(ref reader);",
        "    public static SourceTypeAlias[] ReadAliases(ref Utf8JsonReader reader) => ReadArray(ref reader, ReadSourceTypeAlias);",
        "    public static void Write(Utf8JsonWriter writer, SourceTypeAlias value) => WriteSourceTypeAlias(writer, value);", ""]

    def cs_read(shape):
        if shape.get("x-dotkt-codec") == "literalText":
            return "LiteralText.ReadJson(ref reader)"
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            return f'Read{target["x-dotkt-model"]["name"]}(ref reader)' if "x-dotkt-model" in target else cs_read(target)
        if shape.get("type") == "array":
            item = shape["items"]
            if "$ref" in item:
                target = definitions[item["$ref"].removeprefix("#/$defs/")]
                read = "Read" + target["x-dotkt-model"]["name"]
            else:
                read = {"string": "ReadString", "integer": "ReadInt32", "boolean": "ReadBoolean"}[item["type"]]
            return f"ReadArray(ref reader, {read})"
        return {"string": "ReadString", "int": "ReadInt32", "long": "ReadInt64", "bool": "ReadBoolean"}[cs_type(shape)] + "(ref reader)"

    def cs_write(shape, expression, indent):
        if shape.get("x-dotkt-codec") == "literalText":
            return [f"{indent}LiteralText.WriteJson(writer, {expression});"]
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            if "x-dotkt-model" in target:
                return [f'{indent}Write{target["x-dotkt-model"]["name"]}(writer, {expression});']
            return cs_write(target, expression, indent)
        if shape.get("type") == "array":
            return [f"{indent}writer.WriteStartArray();", f"{indent}foreach (var item in {expression})", f"{indent}{{",
                    *cs_write(shape["items"], "item", indent + "    "), f"{indent}}}", f"{indent}writer.WriteEndArray();"]
        if "const" in shape:
            return [f'{indent}writer.WriteStringValue({json.dumps(shape["const"])});']
        method = {"string": "WriteStringValue", "int": "WriteNumberValue", "long": "WriteNumberValue", "bool": "WriteBooleanValue"}[cs_type(shape)]
        return [f"{indent}writer.{method}({expression});"]

    def cs_record(name, definition, parent=None, common=()):
        parameters = [cs_parameter(*field) for field in parameters_in_order(definition)]
        suffix = f' : {parent}({", ".join(cs_name(key) for key, _, _ in common)})' if parent else ""
        return f'public sealed record {name}({", ".join(parameters)}){suffix};'

    def parameters_in_order(definition):
        return sorted(parameters(definition), key=lambda field: field[2])

    def cs_data_equal(shape, left, right, depth=0):
        if "$ref" in shape:
            target = definitions[shape["$ref"].removeprefix("#/$defs/")]
            if "x-dotkt-model" in target:
                return f"Equal({left}, {right})"
            return cs_data_equal(target, left, right, depth)
        if shape.get("type") == "array":
            item_left, item_right = f"left{depth}", f"right{depth}"
            item = cs_data_equal(shape["items"], item_left, item_right, depth + 1)
            return f"ArrayEqual({left}, {right}, ({item_left}, {item_right}) => {item})"
        scalar = cs_type(shape)
        if scalar == "string":
            return f"global::System.String.Equals({left}, {right}, global::System.StringComparison.Ordinal)"
        if scalar in {"bool", "int", "long"}:
            return f"{left} == {right}"
        raise ValueError(f"Unsupported source metadata equality scalar: {scalar}")

    def cs_equal_record(name, definition):
        comparisons = [cs_data_equal(shape, f"left.{cs_name(key)}", f"right.{cs_name(key)}")
                       for key, shape, _ in parameters_in_order(definition)]
        lines = [f"    internal static bool Equal({name}? left, {name}? right) =>",
                 "        global::System.Object.ReferenceEquals(left, right)",
                 "        || left is not null && right is not null"]
        lines.extend(f"            && {comparison}" for comparison in comparisons)
        lines[-1] += ";"
        return [*lines, ""]

    def cs_case(name, definition):
        properties = definition["properties"]
        fields = parameters_in_order(definition)
        cs_codec.extend([f"    private static {name} Read{name.replace('.', '')}(ref Utf8JsonReader reader)", "    {",
                         "        Require(ref reader, JsonTokenType.StartObject);", "        ulong seen = 0;"])
        for key, shape, optional in fields:
            value_type = cs_type(shape)
            reference = value_type not in {"bool", "int", "long"}
            cs_codec.append(f'        {value_type}{"?" if optional or reference else ""} {key} = default;')
        cs_codec.extend(["        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)", "        {",
                         "            Require(ref reader, JsonTokenType.PropertyName);", "            var key = reader.GetString();",
                         "            if (!reader.Read()) throw new FormatException();", "            switch (key)", "            {"])
        required_mask = 0
        for index, (key, shape) in enumerate(properties.items()):
            bit = 1 << index
            if key in definition["required"]:
                required_mask |= bit
            cs_codec.extend([f'                case {json.dumps(key)}:', f"                    Seen(ref seen, {bit}UL);"])
            if "const" in shape:
                cs_codec.append(f'                    if (ReadString(ref reader) != {json.dumps(shape["const"])}) throw new FormatException();')
            else:
                cs_codec.append(f"                    {key} = {cs_read(shape)};")
            cs_codec.extend(["                    break;"])
        cs_codec.extend(["                default: throw new FormatException();", "            }", "        }",
                         "        Require(ref reader, JsonTokenType.EndObject);",
                         f"        if ((seen & {required_mask}UL) != {required_mask}UL) throw new FormatException();"])
        args = ", ".join(key + ("!" if not optional and cs_type(shape) not in {"bool", "int", "long"} else "")
                         for key, shape, optional in fields)
        cs_codec.extend([f"        return new {name}({args});", "    }", "",
                         f"    private static void Write{name.replace('.', '')}(Utf8JsonWriter writer, {name} value)", "    {",
                         "        writer.WriteStartObject();"])
        for key, shape in properties.items():
            optional = key not in definition["required"]
            indent = "            " if optional else "        "
            if optional:
                cs_codec.extend([f"        if (value.{cs_name(key)} is {{ }} {key})", "        {"])
            cs_codec.append(f"{indent}writer.WritePropertyName({json.dumps(key)});")
            cs_codec.extend(cs_write(shape, key if optional else f"value.{cs_name(key)}", indent))
            if optional:
                cs_codec.append("        }")
        cs_codec.extend(["        writer.WriteEndObject();", "    }", ""])

    for definition in named:
        info = definition["x-dotkt-model"]
        name = info["name"]
        if "discriminator" in info:
            cases = list(variants(definition))
            common = [field for field in parameters_in_order(cases[0][1])
                      if all(field in parameters(shape) for _, shape in cases[1:])]
            cs_model.extend([f'public abstract record {name}({", ".join(cs_parameter(*field) for field in common)})', "{"])
            for variant, shape in cases:
                cs_model.append("    " + cs_record(variant, shape, name, common))
            cs_model.extend(["}", ""])
            cs_codec.extend([f"    private static {name} Read{name}(ref Utf8JsonReader reader) =>",
                             f'        Discriminator(reader, {json.dumps(info["discriminator"])}) switch', "        {"])
            for variant, shape in cases:
                tag = shape["properties"][info["discriminator"]]["const"]
                cs_codec.append(f'            {json.dumps(tag)} => Read{name}{variant}(ref reader),')
            cs_codec.extend(["            _ => throw new FormatException(),", "        };", "",
                             f"    private static void Write{name}(Utf8JsonWriter writer, {name} value)", "    {", "        switch (value)", "        {"])
            for variant, _ in cases:
                cs_codec.append(f"            case {name}.{variant} node: Write{name}{variant}(writer, node); break;")
            cs_codec.extend(["            default: throw new FormatException();", "        }", "    }", ""])
            for variant, shape in cases:
                cs_case(name + "." + variant, shape)
        else:
            cs_model.extend([cs_record(name, definition), ""])
            cs_case(name, definition)
    cs_model.extend(["// Logical data equality includes every maintained field and nested vector.",
                     "// Record equality alone would compare array storage rather than its contents.",
                     "internal static class SourceMetadataDataEquality", "{"])
    for definition in named:
        info = definition["x-dotkt-model"]
        name = info["name"]
        if "discriminator" in info:
            cases = list(variants(definition))
            cs_model.extend([f"    internal static bool Equal({name}? left, {name}? right)", "    {",
                             "        if (global::System.Object.ReferenceEquals(left, right)) return true;",
                             "        return (left, right) switch", "        {"])
            for variant, _ in cases:
                cs_model.append(f"            ({name}.{variant} first, {name}.{variant} second) => Equal(first, second),")
            cs_model.extend(["            _ => false,", "        };", "    }", ""])
            for variant, shape in cases:
                cs_model.extend(cs_equal_record(name + "." + variant, shape))
        else:
            cs_model.extend(cs_equal_record(name, definition))
    cs_model.extend(['''    private static bool ArrayEqual<T>(T[]? left, T[]? right, global::System.Func<T, T, bool> equal)
    {
        if (global::System.Object.ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!equal(left[index], right[index])) return false;
        return true;
    }
}''', ""])
    cs_codec.extend(['''    private delegate T Reader<T>(ref Utf8JsonReader reader);
    private static T[] ReadArray<T>(ref Utf8JsonReader reader, Reader<T> read)
    {
        Require(ref reader, JsonTokenType.StartArray);
        var items = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) items.Add(read(ref reader));
        Require(ref reader, JsonTokenType.EndArray);
        return items.ToArray();
    }
    private static string Discriminator(Utf8JsonReader reader, string property)
    {
        Require(ref reader, JsonTokenType.StartObject);
        string? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Require(ref reader, JsonTokenType.PropertyName);
            var key = reader.GetString();
            if (!reader.Read()) throw new FormatException();
            if (key == property)
            {
                if (result != null) throw new FormatException();
                result = ReadString(ref reader);
            }
            else reader.Skip();
        }
        Require(ref reader, JsonTokenType.EndObject);
        return result ?? throw new FormatException();
    }
    private static void Seen(ref ulong seen, ulong bit)
    {
        if ((seen & bit) != 0) throw new FormatException();
        seen |= bit;
    }
    private static void Require(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (reader.TokenType != token) throw new FormatException();
    }
    private static string ReadString(ref Utf8JsonReader reader)
    {
        Require(ref reader, JsonTokenType.String);
        return reader.GetString()!;
    }
    private static int ReadInt32(ref Utf8JsonReader reader)
    {
        Require(ref reader, JsonTokenType.Number);
        return reader.TryGetInt32(out var value) ? value : throw new FormatException();
    }
    private static long ReadInt64(ref Utf8JsonReader reader)
    {
        Require(ref reader, JsonTokenType.Number);
        return reader.TryGetInt64(out var value) ? value : throw new FormatException();
    }
    private static bool ReadBoolean(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => true, JsonTokenType.False => false, _ => throw new FormatException(),
    };
}''', ""])
    return {
        **messagepack_outputs(root, named, definitions, variants, parameters, cs_type, cs_name, type_name),
        root / "toolchain/kotc/src/main/kotlin/kotc/bir/SourceMetadata.kt": "\n".join(model),
        root / "toolchain/kotc/src/main/kotlin/kotc/bir/SourceMetadataJson.kt": "\n".join(writer),
        root / "toolchain/bir-common/SourceMetadata.Model.Generated.cs": "\n".join(cs_model),
        root / "toolchain/bir-common/SourceMetadata.Json.Generated.cs": "\n".join(cs_codec),
    }
