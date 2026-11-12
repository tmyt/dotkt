"""MessagePack boundary codecs over the source schema's normalized model cases."""
import json


def outputs(root, named, definitions, variants, parameters, cs_type, cs_name, kt_type):
    header = "// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py."
    cs = [header, "#nullable enable", "using System;", "using System.Collections.Generic;", "using MessagePack;",
          "namespace DotKt.Bir;", "", "public static class SourceMetadataMessagePack", "{",
          "    public static SourceTypeAlias Read(ref MessagePackReader reader) => ReadSourceTypeAlias(ref reader);",
          "    public static void Write(ref MessagePackWriter writer, SourceTypeAlias value) => WriteSourceTypeAlias(ref writer, value, 0);",
          "    public static void Write(ref MessagePackWriter writer, SourceTypeAlias value, int depth) => WriteSourceTypeAlias(ref writer, value, depth);", ""]
    kt = [header, "package kotc.bir", "", "import org.msgpack.core.MessagePack", "import org.msgpack.core.MessagePacker", "",
          "internal object SourceMetadataMessagePack {",
          "    fun encode(value: SourceTypeAlias): ByteArray = MessagePack.newDefaultBufferPacker().use { packer ->",
          "        writeSourceTypeAlias(value, packer)", "        packer.toByteArray()", "    }",
          "    fun write(value: SourceTypeAlias, packer: MessagePacker) = writeSourceTypeAlias(value, packer)", ""]

    def target(shape):
        return definitions[shape["$ref"].removeprefix("#/$defs/")]

    def cs_read(shape):
        if "$ref" in shape:
            definition = target(shape)
            return f'Read{definition["x-dotkt-model"]["name"]}(ref reader)' if "x-dotkt-model" in definition else cs_read(definition)
        if shape.get("type") == "array":
            item = shape["items"]
            method = "Read" + target(item)["x-dotkt-model"]["name"] if "$ref" in item else "ReadText"
            return f"ReadArray(ref reader, {method})"
        return {"string": "ReadText(ref reader)", "int": "reader.ReadInt32()", "long": "reader.ReadInt64()",
                "bool": "reader.ReadBoolean()"}[cs_type(shape)]

    def cs_write(shape, value, indent, level=1):
        if "$ref" in shape:
            definition = target(shape)
            if "x-dotkt-model" in definition:
                return [f'{indent}Write{definition["x-dotkt-model"]["name"]}(ref writer, {value}, depth + {level});']
            return cs_write(definition, value, indent, level)
        if shape.get("type") == "array":
            return [f"{indent}if (depth + {level} >= BirJson.MaxDepth) throw new FormatException();",
                    f"{indent}writer.WriteArrayHeader({value}.Length);", f"{indent}foreach (var item in {value})", f"{indent}{{",
                    *cs_write(shape["items"], "item", indent + "    ", level + 1), f"{indent}}}"]
        if "const" in shape:
            return [f'{indent}writer.Write({json.dumps(shape["const"])});']
        method = "IrMessagePack.WriteString(ref writer, " if cs_type(shape) == "string" else "writer.Write("
        return [f"{indent}{method}{value});"]

    def kt_write(shape, value, indent):
        if "$ref" in shape:
            definition = target(shape)
            if "x-dotkt-model" in definition:
                return [f'{indent}write{definition["x-dotkt-model"]["name"]}({value}, packer)']
            return kt_write(definition, value, indent)
        if shape.get("type") == "array":
            return [f"{indent}packer.packArrayHeader({value}.size)", f"{indent}{value}.forEach {{ item ->",
                    *kt_write(shape["items"], "item", indent + "    "), f"{indent}}}"]
        if "const" in shape:
            return [f'{indent}packer.packString({json.dumps(shape["const"])});']
        if kt_type(shape) == "String":
            return [f"{indent}MessagePackText.write(packer, {value})"]
        method = {"Int": "packInt", "Long": "packLong", "Boolean": "packBoolean"}[kt_type(shape)]
        return [f"{indent}packer.{method}({value})"]

    def record(name, definition):
        fields = sorted(parameters(definition), key=lambda field: field[2])
        properties = definition["properties"]
        cs.extend([f"    private static {name} Read{name.replace('.', '')}(ref MessagePackReader reader)", "    {",
                   "        var count = ReadMap(ref reader);", "        ulong seen = 0;"])
        for key, shape, optional in fields:
            value_type = cs_type(shape)
            nullable = optional or value_type not in {"bool", "int", "long"}
            cs.append(f'        {value_type}{"?" if nullable else ""} {key} = default;')
        cs.extend(["        for (var i = 0; i < count; i++)", "        {", "            switch (ReadText(ref reader))", "            {"])
        required_mask = 0
        for index, (key, shape) in enumerate(properties.items()):
            bit = 1 << index
            if key in definition["required"]:
                required_mask |= bit
            cs.extend([f'                case {json.dumps(key)}:', f"                    Seen(ref seen, {bit}UL);"])
            if "const" in shape:
                cs.append(f'                    if (ReadText(ref reader) != {json.dumps(shape["const"])}) throw new FormatException();')
            else:
                cs.append(f"                    {key} = {cs_read(shape)};")
            cs.append("                    break;")
        args = ", ".join(key + ("!" if not optional and cs_type(shape) not in {"bool", "int", "long"} else "")
                         for key, shape, optional in fields)
        cs.extend(["                default: throw new FormatException();", "            }", "        }",
                   f"        if ((seen & {required_mask}UL) != {required_mask}UL) throw new FormatException();",
                   "        reader.Depth--;", f"        return new {name}({args});", "    }", "",
                   f"    private static void Write{name.replace('.', '')}(ref MessagePackWriter writer, {name} value, int depth)", "    {",
                   "        if (depth >= BirJson.MaxDepth) throw new FormatException();"])
        required = len(definition["required"])
        optional = [key for key in properties if key not in definition["required"]]
        count = str(required) + "".join(f" + (value.{cs_name(key)} is null ? 0 : 1)" for key in optional)
        cs.append(f"        writer.WriteMapHeader({count});")
        for key, shape in properties.items():
            optional = key not in definition["required"]
            indent = "            " if optional else "        "
            if optional:
                cs.extend([f"        if (value.{cs_name(key)} is {{ }} {key})", "        {"])
            cs.append(f"{indent}writer.Write({json.dumps(key)});")
            cs.extend(cs_write(shape, key if optional else f"value.{cs_name(key)}", indent))
            if optional:
                cs.append("        }")
        cs.extend(["    }", ""])
        kt.extend([f"    private fun write{name.replace('.', '')}(value: {name}, packer: MessagePacker) {{"])
        count = str(required) + "".join(f" + (if (value.{key} == null) 0 else 1)" for key in properties if key not in definition["required"])
        kt.append(f"        packer.packMapHeader({count})")
        for key, shape in properties.items():
            optional = key not in definition["required"]
            indent = "            " if optional else "        "
            if optional:
                kt.append(f"        value.{key}?.let {{ child ->")
            kt.append(f"{indent}packer.packString({json.dumps(key)})")
            kt.extend(kt_write(shape, "child" if optional else f"value.{key}", indent))
            if optional:
                kt.append("        }")
        kt.extend(["    }", ""])

    for definition in named:
        info = definition["x-dotkt-model"]
        name = info["name"]
        if "discriminator" in info:
            cases = list(variants(definition))
            cs.extend([f"    private static {name} Read{name}(ref MessagePackReader reader) =>",
                       f'        Discriminator(reader, {json.dumps(info["discriminator"])}) switch', "        {"])
            for variant, shape in cases:
                tag = shape["properties"][info["discriminator"]]["const"]
                cs.append(f'            {json.dumps(tag)} => Read{name}{variant}(ref reader),')
            cs.extend(["            _ => throw new FormatException(),", "        };", "",
                       f"    private static void Write{name}(ref MessagePackWriter writer, {name} value, int depth)", "    {", "        switch (value)", "        {"])
            kt.extend([f"    private fun write{name}(value: {name}, packer: MessagePacker) {{", "        when (value) {"])
            for variant, _ in cases:
                cs.append(f"            case {name}.{variant} node: Write{name}{variant}(ref writer, node, depth); break;")
                kt.append(f"            is {name}.{variant} -> write{name}{variant}(value, packer)")
            cs.extend(["            default: throw new FormatException();", "        }", "    }", ""])
            kt.extend(["        }", "    }", ""])
            for variant, shape in cases:
                record(name + "." + variant, shape)
        else:
            record(name, definition)
    cs.extend(['''    private delegate T Reader<T>(ref MessagePackReader reader);
    private static T[] ReadArray<T>(ref MessagePackReader reader, Reader<T> read)
    {
        Enter(ref reader);
        var count = reader.ReadArrayHeader();
        var values = new T[count];
        for (var i = 0; i < count; i++) values[i] = read(ref reader);
        reader.Depth--;
        return values;
    }
    private static int ReadMap(ref MessagePackReader reader)
    {
        Enter(ref reader);
        return reader.ReadMapHeader();
    }
    private static void Enter(ref MessagePackReader reader)
    {
        if (reader.Depth++ >= BirJson.MaxDepth) throw new FormatException();
    }
    private static string Discriminator(MessagePackReader reader, string property)
    {
        var count = ReadMap(ref reader);
        string? result = null;
        for (var i = 0; i < count; i++)
        {
            if (ReadText(ref reader) == property)
            {
                if (result != null) throw new FormatException();
                result = ReadText(ref reader);
            }
            else reader.Skip();
        }
        return result ?? throw new FormatException();
    }
    private static void Seen(ref ulong seen, ulong bit)
    {
        if ((seen & bit) != 0) throw new FormatException();
        seen |= bit;
    }
    private static string ReadText(ref MessagePackReader reader) => IrMessagePack.ReadString(ref reader);
}''', ""])
    kt.extend(["}", ""])
    return {
        root / "toolchain/bir-common/SourceMetadata.MessagePack.Generated.cs": "\n".join(cs),
        root / "toolchain/kotc/src/main/kotlin/kotc/bir/SourceMetadataMessagePack.kt": "\n".join(kt),
    }
