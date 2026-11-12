// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
#nullable enable
using MessagePack;
namespace DotKt.Bir;

public static class BirLiteralMessagePack
{
    public static BirLiteral Read(ref MessagePackReader reader) => reader.NextMessagePackType switch
    {
        MessagePackType.Nil => ReadNull(ref reader),
        MessagePackType.Boolean => new BirLiteral.BooleanLiteral(reader.ReadBoolean()),
        MessagePackType.Integer => ReadInteger(ref reader),
        MessagePackType.Float => new BirLiteral.RealLiteral(reader.ReadDouble()),
        MessagePackType.String or MessagePackType.Extension => new BirLiteral.TextLiteral(IrMessagePack.ReadString(ref reader)),
        MessagePackType.Map => ReadTagged(ref reader),
        _ => throw new FormatException(),
    };

    static BirLiteral ReadNull(ref MessagePackReader reader) { reader.ReadNil(); return new BirLiteral.NullLiteral(); }
    static BirLiteral ReadInteger(ref MessagePackReader reader)
    {
        if (reader.NextCode != MessagePackCode.UInt64) return new BirLiteral.IntegerLiteral(reader.ReadInt64());
        var value = reader.ReadUInt64();
        return value <= long.MaxValue ? new BirLiteral.IntegerLiteral((long)value) : new BirLiteral.UnsignedIntegerLiteral(value);
    }

    static BirLiteral ReadTagged(ref MessagePackReader reader)
    {
        if (reader.Depth >= BirJson.MaxDepth) throw new FormatException();
        reader.Depth++;
        if (reader.ReadMapHeader() != 2) throw new FormatException();
        string? encoding = null;
        long bits = 0;
        bool hasBits = false;
        for (var index = 0; index < 2; index++)
            switch (IrMessagePack.ReadString(ref reader))
            {
                case "encoding" when encoding is null: encoding = IrMessagePack.ReadString(ref reader); break;
                case "bits" when !hasBits: bits = reader.ReadInt64(); hasBits = true; break;
                default: throw new FormatException();
            }
        if (!hasBits) throw new FormatException();
        reader.Depth--;
        return encoding switch
        {
            "f32" => new Float32Literal(checked((int)bits)),
            "f64" => new Float64Literal(bits),
            _ => throw new FormatException(),
        };
    }

    public static void Write(ref MessagePackWriter writer, BirLiteral value, int depth = 0)
    {
        switch (value)
        {
            case BirLiteral.NullLiteral: writer.WriteNil(); break;
            case BirLiteral.BooleanLiteral scalar: writer.Write(scalar.Value); break;
            case BirLiteral.IntegerLiteral scalar: writer.Write(scalar.Value); break;
            case BirLiteral.UnsignedIntegerLiteral scalar: writer.Write(scalar.Value); break;
            case BirLiteral.RealLiteral scalar: writer.Write(scalar.Value); break;
            case BirLiteral.TextLiteral scalar: IrMessagePack.WriteString(ref writer, scalar.Value); break;
            case Float32Literal scalar:
                if (depth >= BirJson.MaxDepth) throw new FormatException();
                writer.WriteMapHeader(2); writer.Write("encoding");
                writer.Write("f32");
                writer.Write("bits"); writer.Write(scalar.Bits); break;
            case Float64Literal scalar:
                if (depth >= BirJson.MaxDepth) throw new FormatException();
                writer.WriteMapHeader(2); writer.Write("encoding");
                writer.Write("f64");
                writer.Write("bits"); writer.Write(scalar.Bits); break;
            default: throw new FormatException();
        }
    }
}
