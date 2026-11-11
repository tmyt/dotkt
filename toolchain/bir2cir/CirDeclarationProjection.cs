#nullable enable
using System.Text.Json.Nodes;
using DotKt.Cir;

// The last physical-declaration boundary while owning bir2cir passes remain DOM-backed.
// The generated projection knows each declaration role; executable trees and opaque payloads are not traversed.
static partial class CirDeclarationProjection
{
    static OptionalField<T> ReadOptional<T>(JsonNode node, string key, Func<JsonNode, T> read)
    {
        if (!node.AsObject().TryGetPropertyValue(key, out var value)) return default;
        return value is null ? OptionalField<T>.Null : OptionalField<T>.Of(read(value));
    }

    static void CheckFields(JsonNode node, string[] fields)
    {
        foreach (var pair in node.AsObject())
            if (Array.IndexOf(fields, pair.Key) < 0)
                throw new InvalidOperationException($"Unprojected CIR declaration field '{pair.Key}'");
    }
}
