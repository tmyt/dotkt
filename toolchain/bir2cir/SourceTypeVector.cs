using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// An immutable declaration-frame signature, not executable children. Frozen text
// is bookkeeping for the remaining ForeignStarProjectionBinding DOM frontier;
// that frontier retires with its owner, not a new IR text hand-off between passes.
sealed class SourceTypeVector
{
    internal IReadOnlyList<TypeNode> Types { get; }
    internal string FrozenText { get; }
    SourceTypeVector(TypeNode[] types, string text) { Types = types; FrozenText = text; }

    internal static SourceTypeVector Create(IReadOnlyList<TypeNode> types)
    {
        var copy = types.Select(BirGraphValue.CopyType).ToArray();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var type in copy) TypeNode.WriteJson(writer, type);
            writer.WriteEndArray();
        }
        return new(copy, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    internal static SourceTypeVector Read(ref Utf8JsonReader reader) => ReadFrozenText(reader.GetString());
    internal static SourceTypeVector ReadFrozenText(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new FormatException();
        var types = new List<TypeNode>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) types.Add(TypeNode.ReadJson(ref reader));
        if (reader.TokenType != JsonTokenType.EndArray || reader.Read()) throw new FormatException();
        return new(types.ToArray(), text);
    }
    internal static void Write(Utf8JsonWriter writer, SourceTypeVector value) => writer.WriteStringValue(value.FrozenText);
    internal static JsonNode ToDom(SourceTypeVector value) => JsonValue.Create(value.FrozenText);
}
