using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// A physical dispatch plan, indexed in its declaration's actual parameter
// frame. This is not a Kotlin source-bounds or round-trip header capability.
sealed class OwnerConstraintDispatchBounds
{
    readonly string text;
    internal IReadOnlyDictionary<int, TypeNode[]> Bounds { get; }

    internal OwnerConstraintDispatchBounds(IReadOnlyDictionary<int, TypeNode[]> bounds)
        : this(bounds, null) { }

    OwnerConstraintDispatchBounds(IReadOnlyDictionary<int, TypeNode[]> bounds, string text)
    {
        Bounds = new ReadOnlyDictionary<int, TypeNode[]>(bounds.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(BirGraphValue.CopyType).ToArray()));
        this.text = text;
    }

    internal static OwnerConstraintDispatchBounds Read(ref Utf8JsonReader reader) => ReadFrozenText(reader.GetString());

    internal static OwnerConstraintDispatchBounds ReadFrozenText(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new FormatException();
        var bounds = new Dictionary<int, TypeNode[]>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName
                || !int.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || bounds.ContainsKey(index) || !reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                throw new FormatException();
            var values = new List<TypeNode>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) values.Add(TypeNode.ReadJson(ref reader));
            if (reader.TokenType != JsonTokenType.EndArray) throw new FormatException();
            bounds.Add(index, values.ToArray());
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new FormatException();
        return new OwnerConstraintDispatchBounds(bounds, text);
    }

    internal string FrozenText
    {
        get
        {
            if (text != null) return text;
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var (index, bounds) in Bounds)
                {
                    writer.WriteStartArray(index.ToString(CultureInfo.InvariantCulture));
                    foreach (var bound in bounds) TypeNode.WriteJson(writer, bound);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    internal static void Write(Utf8JsonWriter writer, OwnerConstraintDispatchBounds value) => writer.WriteStringValue(value.FrozenText);
    internal static JsonNode ToDom(OwnerConstraintDispatchBounds value) => JsonValue.Create(value.FrozenText);
}
