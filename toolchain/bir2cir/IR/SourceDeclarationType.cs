using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A frozen declaration-frame leaf. The text is only its boundary encoding;
// semantic consumers use Type, decoded once with the shared TypeNode codec.
sealed class SourceDeclarationType
{
    internal TypeNode Type { get; }
    internal string FrozenText { get; }
    SourceDeclarationType(TypeNode type, string text) { Type = type; FrozenText = text; }
    internal static SourceDeclarationType Create(TypeNode type) => ReadFrozenText(TypeNode.ToJson(type));
    internal static SourceDeclarationType ReadFrozenText(string text) => new(TypeNode.Parse(text), text);
    internal static SourceDeclarationType Read(ref Utf8JsonReader reader) => ReadFrozenText(reader.GetString());
    internal static void Write(Utf8JsonWriter writer, SourceDeclarationType value) => writer.WriteStringValue(value.FrozenText);
    internal static JsonNode ToDom(SourceDeclarationType value) => JsonValue.Create(value.FrozenText);
}
