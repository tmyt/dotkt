using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Source constraints are typed facts. The textual leaf is solely the existing
// frozen metadata hand-off to consumers that have not yet moved off the DOM.
sealed class SourceBoundsSnapshot
{
    internal IReadOnlyDictionary<int, TypeNode[]> Bounds { get; }
    internal SourceBoundsSnapshot(IReadOnlyDictionary<int, TypeNode[]> bounds)
        => Bounds = new ReadOnlyDictionary<int, TypeNode[]>(bounds.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));

    internal SourceBoundsSnapshot Merge(IReadOnlyDictionary<int, TypeNode[]> additions)
    {
        var merged = Bounds.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in additions) merged.TryAdd(pair.Key, pair.Value);
        return new SourceBoundsSnapshot(merged);
    }

    internal static SourceBoundsSnapshot Read(ref Utf8JsonReader reader)
    {
        var content = Encoding.UTF8.GetBytes(reader.GetString());
        var inner = new Utf8JsonReader(content, new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!inner.Read() || inner.TokenType != JsonTokenType.StartObject) throw new FormatException();
        var bounds = new Dictionary<int, TypeNode[]>();
        if (!inner.Read() || inner.TokenType != JsonTokenType.PropertyName || inner.GetString() != "bounds"
            || !inner.Read() || inner.TokenType != JsonTokenType.StartObject) throw new FormatException();
        while (inner.Read() && inner.TokenType != JsonTokenType.EndObject)
        {
            if (inner.TokenType != JsonTokenType.PropertyName) throw new FormatException();
            var index = int.Parse(inner.GetString(), CultureInfo.InvariantCulture);
            if (!inner.Read() || inner.TokenType != JsonTokenType.StartArray) throw new FormatException();
            var constraints = new List<TypeNode>();
            while (inner.Read() && inner.TokenType != JsonTokenType.EndArray) constraints.Add(TypeNode.ReadJson(ref inner));
            if (inner.TokenType != JsonTokenType.EndArray || !bounds.TryAdd(index, constraints.ToArray())) throw new FormatException();
        }
        if (inner.TokenType != JsonTokenType.EndObject || !inner.Read() || inner.TokenType != JsonTokenType.EndObject
            || inner.Read()) throw new FormatException();
        return new SourceBoundsSnapshot(bounds);
    }

    string ToJson()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = BirJson.MaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("bounds");
            foreach (var (index, constraints) in Bounds)
            {
                writer.WriteStartArray(index.ToString(CultureInfo.InvariantCulture));
                foreach (var constraint in constraints) TypeNode.WriteJson(writer, constraint);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static void Write(Utf8JsonWriter writer, SourceBoundsSnapshot value) => writer.WriteStringValue(value.ToJson());
    internal static JsonNode ToDom(SourceBoundsSnapshot value) => JsonValue.Create(value.ToJson());

    internal static void SelfTest()
    {
        var original = new TypeNode[] { new TypeNode.Fqn("Witness.日本", new TypeNode[] { new TypeNode.Tv("method", 1) }) };
        var snapshot = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = original });
        var merged = snapshot.Merge(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Fqn("Later") },
            [3] = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("type", 2)) },
        });
        original[0] = new TypeNode.Fqn("MutatedInput");
        if (snapshot.Bounds.Count != 1 || merged.Bounds.Count != 2
            || snapshot.Bounds[0][0] is not TypeNode.Fqn { Name: "Witness.日本" }
            || merged.Bounds[0][0] != snapshot.Bounds[0][0])
            throw new InvalidOperationException("Source bound snapshot lost input isolation or earliest source facts");
        var bytes = Encoding.UTF8.GetBytes(ToDom(merged).ToJsonString(BirJson.Writer));
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read()) throw new InvalidOperationException();
        var roundtrip = Read(ref reader);
        if (reader.Read() || ToDom(roundtrip).GetValue<string>() != ToDom(merged).GetValue<string>()
            || roundtrip.Bounds[3].Single() != new TypeNode.Nullable(new TypeNode.Tv("type", 2)))
            throw new InvalidOperationException("Source bound snapshot changed frames, positions or Unicode at its frontier");
        Console.WriteLine("[source bounds snapshot] self-test OK (typed source facts, first-writer wins, input isolation, exact leaf codec)");
    }
}
