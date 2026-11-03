#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>Lossless UTF-16 literal payloads, independent of Kotlin/CLR type representation.
/// Scalar-valid strings use JSON strings. Strings with unpaired surrogates carry explicit code units;
/// escaping an unpaired surrogate inside a JSON string does not survive System.Text.Json.</summary>
public static class LiteralText
{
    public static JsonNode Encode(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]) &&
                (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i])))
                return new JsonObject
                {
                    ["encoding"] = "utf16",
                    ["units"] = new JsonArray(text.Select(c => (JsonNode?)JsonValue.Create((int)c)).ToArray()),
                };
        return JsonValue.Create(text)!;
    }

    public static string Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.GetProperty("encoding").GetString() != "utf16") throw new JsonException("Unknown literal text encoding");
        return new string(value.GetProperty("units").EnumerateArray().Select(u => (char)u.GetUInt16()).ToArray());
    }

}
