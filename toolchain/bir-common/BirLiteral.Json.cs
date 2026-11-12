#nullable enable
using System.Text.Json;

namespace DotKt.Bir;

public abstract partial record BirLiteral
{
    public static BirLiteral ReadJson(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return new NullLiteral();
            case JsonTokenType.True: return new BooleanLiteral(true);
            case JsonTokenType.False: return new BooleanLiteral(false);
            case JsonTokenType.String: return new TextLiteral(reader.GetString()!);
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var signed))
                    return signed == 0 && double.IsNegative(reader.GetDouble())
                        ? new RealLiteral(reader.GetDouble()) : new IntegerLiteral(signed);
                return reader.TryGetUInt64(out var unsigned)
                    ? new UnsignedIntegerLiteral(unsigned) : new RealLiteral(reader.GetDouble());
            case JsonTokenType.StartObject:
                var probe = reader;
                while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)
                {
                    if (probe.TokenType != JsonTokenType.PropertyName) throw new FormatException();
                    var property = probe.GetString();
                    if (!probe.Read()) throw new FormatException();
                    if (property == "encoding" && probe.GetString() == "utf16")
                        return new TextLiteral(LiteralText.ReadJson(ref reader));
                    probe.Skip();
                }
                string? encoding = null;
                long bits = 0;
                int seen = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new FormatException();
                    var property = reader.GetString();
                    if (!reader.Read()) throw new FormatException();
                    switch (property)
                    {
                        case "encoding" when (seen & 1) == 0: encoding = reader.GetString(); seen |= 1; break;
                        case "bits" when (seen & 2) == 0: bits = reader.GetInt64(); seen |= 2; break;
                        default: throw new FormatException();
                    }
                }
                if (reader.TokenType != JsonTokenType.EndObject || seen != 3) throw new FormatException();
                return encoding switch {
                    "f32" => new Float32Literal(checked((int)bits)),
                    "f64" => new Float64Literal(bits),
                    _ => throw new FormatException(),
                };
            default: throw new FormatException();
        }
    }

    public static void WriteJson(Utf8JsonWriter writer, BirLiteral value)
    {
        switch (value)
        {
            case NullLiteral: writer.WriteNullValue(); break;
            case BooleanLiteral boolean: writer.WriteBooleanValue(boolean.Value); break;
            case IntegerLiteral integer: writer.WriteNumberValue(integer.Value); break;
            case UnsignedIntegerLiteral integer: writer.WriteNumberValue(integer.Value); break;
            case RealLiteral real: writer.WriteNumberValue(real.Value); break;
            case TextLiteral text: LiteralText.WriteJson(writer, text.Value); break;
            case Float32Literal real:
                writer.WriteStartObject(); writer.WriteString("encoding", "f32");
                writer.WriteNumber("bits", real.Bits); writer.WriteEndObject(); break;
            case Float64Literal real:
                writer.WriteStartObject(); writer.WriteString("encoding", "f64");
                writer.WriteNumber("bits", real.Bits); writer.WriteEndObject(); break;
            default: throw new InvalidOperationException();
        }
    }
}
