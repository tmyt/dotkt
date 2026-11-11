// Generated from docs/bir-cir.schema.json; run scripts/generate-ir-model.py.
#nullable enable
using System;
using System.Text.Json.Nodes;
namespace DotKt.Bir;

public sealed record Float32Literal(int Bits) : BirLiteral
{
    public override JsonNode ToDom() => new JsonObject { ["encoding"] = "f32", ["bits"] = Bits };
}

public sealed record Float64Literal(long Bits) : BirLiteral
{
    public override JsonNode ToDom() => new JsonObject { ["encoding"] = "f64", ["bits"] = Bits };
}

public abstract partial record BirLiteral
{
    private static BirLiteral ReadFloating(JsonObject value)
    {
        if (value.Count != 2 || value["bits"] is not JsonValue bits) throw new FormatException();
        return value["encoding"]?.GetValue<string>() switch
        {
            "f32" => new Float32Literal(bits.GetValue<int>()),
            "f64" => new Float64Literal(bits.GetValue<long>()),
            _ => throw new FormatException(),
        };
    }
}
