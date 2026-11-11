#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

// Semantic scalar values. No choice of CLR storage or opcode belongs here.
public abstract partial record BirLiteral
{
    public sealed record NullLiteral : BirLiteral;
    public sealed record BooleanLiteral(bool Value) : BirLiteral;
    public sealed record IntegerLiteral(long Value) : BirLiteral;
    public sealed record UnsignedIntegerLiteral(ulong Value) : BirLiteral;
    public sealed record RealLiteral(double Value) : BirLiteral;
    public sealed record TextLiteral(string Value) : BirLiteral;

    public virtual JsonNode? ToDom() => this switch {
        NullLiteral => null,
        BooleanLiteral value => JsonValue.Create(value.Value),
        IntegerLiteral value => JsonValue.Create(value.Value),
        UnsignedIntegerLiteral value => JsonValue.Create(value.Value),
        RealLiteral value => JsonValue.Create(value.Value),
        TextLiteral value => LiteralText.Encode(value.Value),
        _ => throw new InvalidOperationException(),
    };

    // Temporary boundary for the enclosing BIR passes, which still own DOM trees.
    // The scalar itself is decoded once into a closed semantic value before use.
    public static BirLiteral Read(JsonNode? value)
    {
        if (value is null) return new NullLiteral();
        if (value is JsonObject record)
            return record["encoding"]?.GetValue<string>() == "utf16"
                ? new TextLiteral(LiteralText.Read(record)) : ReadFloating(record);
        var scalar = (JsonValue)value;
        if (scalar.TryGetValue<JsonElement>(out var element))
            return element.ValueKind switch {
                JsonValueKind.True => new BooleanLiteral(true),
                JsonValueKind.False => new BooleanLiteral(false),
                JsonValueKind.String => new TextLiteral(element.GetString()!),
                JsonValueKind.Number => ReadNumber(element),
                _ => throw new FormatException(),
            };
        if (scalar.TryGetValue<bool>(out var boolean)) return new BooleanLiteral(boolean);
        if (scalar.TryGetValue<string>(out var text)) return new TextLiteral(text);
        if (scalar.TryGetValue<int>(out var i)) return new IntegerLiteral(i);
        if (scalar.TryGetValue<long>(out var l)) return new IntegerLiteral(l);
        if (scalar.TryGetValue<uint>(out var u)) return new IntegerLiteral(u);
        if (scalar.TryGetValue<ulong>(out var ul)) return new UnsignedIntegerLiteral(ul);
        if (scalar.TryGetValue<byte>(out var b)) return new IntegerLiteral(b);
        if (scalar.TryGetValue<sbyte>(out var sb)) return new IntegerLiteral(sb);
        if (scalar.TryGetValue<short>(out var s)) return new IntegerLiteral(s);
        if (scalar.TryGetValue<ushort>(out var us)) return new IntegerLiteral(us);
        if (scalar.TryGetValue<float>(out var f)) return new Float32Literal(BitConverter.SingleToInt32Bits(f));
        if (scalar.TryGetValue<double>(out var d)) return new Float64Literal(BitConverter.DoubleToInt64Bits(d));
        throw new FormatException();
    }

    static BirLiteral ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            // JSON's -0 is also an integral token, but its numeric sign must survive
            // until the owning layer chooses the physical scalar representation.
            if (integer == 0 && double.IsNegative(element.GetDouble()))
                return new RealLiteral(element.GetDouble());
            return new IntegerLiteral(integer);
        }
        return element.TryGetUInt64(out var unsigned)
            ? new UnsignedIntegerLiteral(unsigned) : new RealLiteral(element.GetDouble());
    }
}
