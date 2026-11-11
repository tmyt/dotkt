#nullable enable
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Cir;

// The final physical CIR boundary while owning bir2cir passes remain DOM-backed.
// Generated declaration and executable readers follow schema roles; opaque source payloads stay bytes.
static partial class CirDeclarationProjection
{
    static void CheckSourceStamp(JsonNode node)
    {
        if (TypeJson.Read(node["sty"]) is not TypeNode stamp) return;
        foreach (var key in new[] { "ret", "dynRet" })
            if (TypeJson.Read(node[key]) is TypeNode result && !IrSanity.StampAgrees(stamp, result))
                throw new InvalidOperationException($"Unconsumed stale result stamp at {node.GetPath()}");
    }

    static OptionalField<T> ReadOptional<T>(JsonNode node, string key, Func<JsonNode, T> read, bool allowNull = true)
    {
        if (!node.AsObject().TryGetPropertyValue(key, out var value)) return default;
        return value is null ? allowNull ? OptionalField<T>.Null : throw new FormatException()
            : OptionalField<T>.Of(read(value));
    }

    static void CheckFields(JsonNode node, string[] fields)
    {
        foreach (var pair in node.AsObject())
            if (Array.IndexOf(fields, pair.Key) < 0)
                throw new InvalidOperationException($"Unprojected CIR field '{pair.Key}' at {node.GetPath()}");
    }
}
