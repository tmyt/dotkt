"""Generate the tagged, width-preserving scalar cases of the semantic BIR literal."""
import json


def outputs(root, definitions):
    shapes = [shape for shape in definitions.values() if isinstance(shape, dict)
              and shape.get("x-dotkt-model", {}).get("domain") == "birFloatingLiteral"]
    cs = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
          "#nullable enable", "using System;", "using System.Text.Json.Nodes;", "namespace DotKt.Bir;", ""]
    kt = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.", "package kotc.bir", ""]
    for shape in shapes:
        if set(shape["properties"]) != {"encoding", "bits"} or set(shape["required"]) != {"encoding", "bits"}:
            raise ValueError("Floating literal models must account for every schema field")
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        width = shape["properties"]["bits"]["x-dotkt-csharp-type"]
        if width not in {"int", "long"}:
            raise ValueError("Unsupported logical floating literal width")
        cs.extend([f"public sealed record {name}({width} Bits) : BirLiteral", "{",
                   f"    public override JsonNode ToDom() => new JsonObject {{ [\"encoding\"] = {tag}, [\"bits\"] = Bits }};", "}", ""])
        prefix = json.dumps('{"encoding":' + tag + ',"bits":')
        kt.extend([f"data class {name}(val bits: {'Int' if width == 'int' else 'Long'}) : BirLiteral() {{",
                   "    override fun writeJson(output: StringBuilder) {",
                   f"        output.append({prefix}).append(bits).append('}}')", "    }", "}", ""])
    cs.extend(["public abstract partial record BirLiteral", "{",
               "    private static BirLiteral ReadFloating(JsonObject value)", "    {",
               "        if (value.Count != 2 || value[\"bits\"] is not JsonValue bits) throw new FormatException();",
               "        return value[\"encoding\"]?.GetValue<string>() switch", "        {"])
    for shape in shapes:
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        width = shape["properties"]["bits"]["x-dotkt-csharp-type"]
        cs.append(f"            {tag} => new {name}(bits.GetValue<{width}>()),")
    cs.extend(["            _ => throw new FormatException(),", "        };", "    }", "}", ""])
    mp = ["// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.",
          "#nullable enable", "using MessagePack;", "namespace DotKt.Bir;", "",
          "public static class BirLiteralMessagePack", "{",
          "    public static BirLiteral Read(ref MessagePackReader reader) => reader.NextMessagePackType switch", "    {",
          "        MessagePackType.Nil => ReadNull(ref reader),",
          "        MessagePackType.Boolean => new BirLiteral.BooleanLiteral(reader.ReadBoolean()),",
          "        MessagePackType.Integer => ReadInteger(ref reader),",
          "        MessagePackType.Float => new BirLiteral.RealLiteral(reader.ReadDouble()),",
          "        MessagePackType.String or MessagePackType.Extension => new BirLiteral.TextLiteral(IrMessagePack.ReadString(ref reader)),",
          "        MessagePackType.Map => ReadTagged(ref reader),", "        _ => throw new FormatException(),", "    };", "",
          "    static BirLiteral ReadNull(ref MessagePackReader reader) { reader.ReadNil(); return new BirLiteral.NullLiteral(); }",
          "    static BirLiteral ReadInteger(ref MessagePackReader reader)", "    {",
          "        if (reader.NextCode != MessagePackCode.UInt64) return new BirLiteral.IntegerLiteral(reader.ReadInt64());",
          "        var value = reader.ReadUInt64();",
          "        return value <= long.MaxValue ? new BirLiteral.IntegerLiteral((long)value) : new BirLiteral.UnsignedIntegerLiteral(value);", "    }", "",
          "    static BirLiteral ReadTagged(ref MessagePackReader reader)", "    {",
          "        if (reader.Depth >= BirJson.MaxDepth) throw new FormatException();", "        reader.Depth++;",
          "        if (reader.ReadMapHeader() != 2) throw new FormatException();",
          "        string? encoding = null;", "        long bits = 0;", "        bool hasBits = false;",
          "        for (var index = 0; index < 2; index++)", "            switch (IrMessagePack.ReadString(ref reader))", "            {",
          '                case "encoding" when encoding is null: encoding = IrMessagePack.ReadString(ref reader); break;',
          '                case "bits" when !hasBits: bits = reader.ReadInt64(); hasBits = true; break;',
          "                default: throw new FormatException();", "            }",
          "        if (!hasBits) throw new FormatException();", "        reader.Depth--;", "        return encoding switch", "        {"]
    for shape in shapes:
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        width = shape["properties"]["bits"]["x-dotkt-csharp-type"]
        mp += [f"            {tag} => new {name}({'checked((int)bits)' if width == 'int' else 'bits'}),"]
    mp += ["            _ => throw new FormatException(),", "        };", "    }", "",
           "    public static void Write(ref MessagePackWriter writer, BirLiteral value, int depth = 0)", "    {",
           "        switch (value)", "        {",
           "            case BirLiteral.NullLiteral: writer.WriteNil(); break;",
           "            case BirLiteral.BooleanLiteral scalar: writer.Write(scalar.Value); break;",
           "            case BirLiteral.IntegerLiteral scalar: writer.Write(scalar.Value); break;",
           "            case BirLiteral.UnsignedIntegerLiteral scalar: writer.Write(scalar.Value); break;",
           "            case BirLiteral.RealLiteral scalar: writer.Write(scalar.Value); break;",
           "            case BirLiteral.TextLiteral scalar: IrMessagePack.WriteString(ref writer, scalar.Value); break;"]
    for shape in shapes:
        name = shape["x-dotkt-model"]["name"]
        tag = json.dumps(shape["properties"]["encoding"]["const"])
        mp += [f"            case {name} scalar:",
               "                if (depth >= BirJson.MaxDepth) throw new FormatException();",
               '                writer.WriteMapHeader(2); writer.Write("encoding");', f"                writer.Write({tag});",
               '                writer.Write("bits"); writer.Write(scalar.Bits); break;']
    mp += ["            default: throw new FormatException();", "        }", "    }", "}", ""]
    return {root / "toolchain/bir-common/BirFloatingLiteral.Generated.cs": "\n".join(cs),
            root / "toolchain/bir-common/BirLiteral.MessagePack.Generated.cs": "\n".join(mp),
            root / "toolchain/kotc/src/main/kotlin/kotc/bir/FloatingLiterals.Generated.kt": "\n".join(kt)}
