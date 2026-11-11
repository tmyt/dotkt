#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>Lossless UTF-16 literal payloads, independent of Kotlin/CLR type representation.
/// Scalar-valid strings use JSON strings. Strings with unpaired surrogates carry explicit code units;
/// escaping an unpaired surrogate inside a JSON string does not survive System.Text.Json.</summary>
public static class LiteralText
{
    public static void WriteJson(Utf8JsonWriter writer, string text)
    {
        if (!RequiresCodeUnits(text)) { writer.WriteStringValue(text); return; }
        writer.WriteStartObject();
        writer.WriteString("encoding", "utf16");
        writer.WritePropertyName("units");
        writer.WriteStartArray();
        foreach (var unit in text) writer.WriteNumberValue((ushort)unit);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static string ReadJson(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
        if (reader.TokenType != JsonTokenType.StartObject) throw new FormatException();
        var seen = 0;
        string? text = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new FormatException();
            var key = reader.GetString();
            if (!reader.Read()) throw new FormatException();
            switch (key)
            {
                case "encoding":
                    if ((seen & 1) != 0 || reader.TokenType != JsonTokenType.String || reader.GetString() != "utf16")
                        throw new FormatException();
                    seen |= 1;
                    break;
                case "units":
                    if ((seen & 2) != 0 || reader.TokenType != JsonTokenType.StartArray) throw new FormatException();
                    seen |= 2;
                    var value = new StringBuilder();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var unit) || (uint)unit > ushort.MaxValue)
                            throw new FormatException();
                        value.Append((char)unit);
                    }
                    if (reader.TokenType != JsonTokenType.EndArray) throw new FormatException();
                    text = value.ToString();
                    break;
                default: throw new FormatException();
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || seen != 3) throw new FormatException();
        return text!;
    }

    public static JsonNode Encode(string text)
        => RequiresCodeUnits(text) ? new JsonObject
        {
            ["encoding"] = "utf16",
            ["units"] = new JsonArray(text.Select(c => (JsonNode?)JsonValue.Create((int)c)).ToArray()),
        } : JsonValue.Create(text)!;

    public static bool RequiresCodeUnits(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]) &&
                (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i])))
                return true;
        return false;
    }

    public static string Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.GetProperty("encoding").GetString() != "utf16") throw new JsonException("Unknown literal text encoding");
        return new string(value.GetProperty("units").EnumerateArray().Select(u => (char)u.GetUInt16()).ToArray());
    }

    public static string Read(JsonNode value)
    {
        if (value is JsonValue text) return text.GetValue<string>();
        if (value["encoding"]?.GetValue<string>() != "utf16") throw new JsonException("Unknown literal text encoding");
        return new string(((JsonArray)value["units"]!).Select(unit => (char)checked((ushort)unit!.GetValue<int>())).ToArray());
    }

}
