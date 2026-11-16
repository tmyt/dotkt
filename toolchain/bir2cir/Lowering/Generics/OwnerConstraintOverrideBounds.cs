using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// The selected override's weakening plan is a frozen scalar metadata leaf,
// not an executable graph or a second declaration representation.
sealed class OwnerConstraintOverrideBounds
{
    internal sealed record Row(int Index, TypeNode Bound, TypeNode Template,
        TypeNode[] Consequences, TypeNode[] OwnerArgs);

    readonly Row[] rows;
    readonly string frozenText;
    internal IReadOnlyList<Row> Rows => rows;

    internal OwnerConstraintOverrideBounds(IEnumerable<Row> rows)
        : this(rows, null) { }

    OwnerConstraintOverrideBounds(IEnumerable<Row> rows, string frozenText)
    {
        this.rows = rows.Select(row => new Row(row.Index, BirGraphValue.CopyType(row.Bound),
            BirGraphValue.CopyType(row.Template), row.Consequences.Select(BirGraphValue.CopyType).ToArray(),
            row.OwnerArgs.Select(BirGraphValue.CopyType).ToArray())).ToArray();
        this.frozenText = frozenText;
    }

    internal OwnerConstraintOverrideBounds Append(IEnumerable<Row> additions) => new(rows.Concat(additions));

    internal static OwnerConstraintOverrideBounds Read(ref Utf8JsonReader reader) => ReadFrozenText(reader.GetString());

    internal static OwnerConstraintOverrideBounds ReadFrozenText(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new FormatException();
        var rows = new List<Row>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new FormatException();
            var fields = 0;
            var index = 0;
            TypeNode bound = null, template = null;
            TypeNode[] consequences = null, ownerArgs = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new FormatException();
                var field = reader.GetString() switch {
                    "index" => 1, "bound" => 2, "template" => 4, "consequences" => 8, "ownerArgs" => 16,
                    _ => throw new FormatException(),
                };
                if ((fields & field) != 0 || !reader.Read()) throw new FormatException();
                fields |= field;
                switch (field)
                {
                    case 1: index = reader.GetInt32(); break;
                    case 2: bound = TypeNode.ReadJson(ref reader); break;
                    case 4: template = TypeNode.ReadJson(ref reader); break;
                    case 8: consequences = Types(ref reader); break;
                    case 16: ownerArgs = Types(ref reader); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || fields != 31) throw new FormatException();
            rows.Add(new Row(index, bound, template, consequences, ownerArgs));
        }
        if (reader.TokenType != JsonTokenType.EndArray || reader.Read()) throw new FormatException();
        return new OwnerConstraintOverrideBounds(rows, text);
    }

    static TypeNode[] Types(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new FormatException();
        var values = new List<TypeNode>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) values.Add(TypeNode.ReadJson(ref reader));
        if (reader.TokenType != JsonTokenType.EndArray) throw new FormatException();
        return values.ToArray();
    }

    internal string FrozenText
    {
        get
        {
            if (frozenText != null) return frozenText;
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartArray();
                foreach (var row in rows)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("index", row.Index);
                    writer.WritePropertyName("bound"); TypeNode.WriteJson(writer, row.Bound);
                    writer.WritePropertyName("template"); TypeNode.WriteJson(writer, row.Template);
                    writer.WriteStartArray("consequences");
                    foreach (var type in row.Consequences) TypeNode.WriteJson(writer, type);
                    writer.WriteEndArray();
                    writer.WriteStartArray("ownerArgs");
                    foreach (var type in row.OwnerArgs) TypeNode.WriteJson(writer, type);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    internal static void Write(Utf8JsonWriter writer, OwnerConstraintOverrideBounds value) => writer.WriteStringValue(value.FrozenText);
    internal static JsonNode ToDom(OwnerConstraintOverrideBounds value) => JsonValue.Create(value.FrozenText);
}
