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

// Declaration-owned source types, before physical representation changes them.
// Text is emitted only for the frozen metadata leaf at the remaining DOM frontier.
sealed class SourceTypeFacts
{
    enum Part { Base, Interfaces, Bounds }
    readonly Part[] order;
    internal static readonly SourceTypeFacts Empty = new(null, null, new Dictionary<int, TypeNode[]>(), Array.Empty<Part>());
    internal TypeNode Base { get; }
    internal IReadOnlyList<TypeNode> Interfaces { get; }
    internal IReadOnlyDictionary<int, TypeNode[]> Bounds { get; }

    internal SourceTypeFacts(IReadOnlyDictionary<int, TypeNode[]> bounds)
        : this(null, null, bounds, new[] { Part.Bounds }) { }

    SourceTypeFacts(TypeNode baseType, IReadOnlyList<TypeNode> interfaces,
        IReadOnlyDictionary<int, TypeNode[]> bounds, IReadOnlyList<Part> order)
    {
        Base = baseType;
        Interfaces = interfaces?.ToArray();
        Bounds = new ReadOnlyDictionary<int, TypeNode[]>(bounds.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        this.order = order.ToArray();
    }

    internal SourceTypeFacts Merge(IReadOnlyDictionary<int, TypeNode[]> bounds) => Merge(null, null, bounds);

    internal SourceTypeFacts Merge(TypeNode baseType, IReadOnlyList<TypeNode> interfaces,
        IReadOnlyDictionary<int, TypeNode[]> bounds)
    {
        if (baseType == null && interfaces is not { Count: > 0 } && bounds is not { Count: > 0 }) return this;
        var parts = order.ToList();
        if (Base == null && baseType != null) parts.Add(Part.Base);
        var mergedInterfaces = Interfaces?.ToList();
        if (interfaces is { Count: > 0 })
        {
            if (mergedInterfaces == null) { mergedInterfaces = new(); parts.Add(Part.Interfaces); }
            foreach (var edge in interfaces)
                if (!mergedInterfaces.Any(prior => prior is TypeNode.Fqn left && edge is TypeNode.Fqn right
                    && left.Name == right.Name && (left.Args?.Length ?? 0) == (right.Args?.Length ?? 0)))
                    mergedInterfaces.Add(edge);
        }
        var mergedBounds = Bounds.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (bounds is { Count: > 0 })
        {
            if (!parts.Contains(Part.Bounds)) parts.Add(Part.Bounds);
            foreach (var pair in bounds) mergedBounds.TryAdd(pair.Key, pair.Value);
        }
        return new SourceTypeFacts(Base ?? baseType, mergedInterfaces, mergedBounds, parts);
    }

    internal static SourceTypeFacts Read(ref Utf8JsonReader reader)
    {
        var content = Encoding.UTF8.GetBytes(reader.GetString());
        var inner = new Utf8JsonReader(content, new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!inner.Read() || inner.TokenType != JsonTokenType.StartObject) throw new FormatException();
        TypeNode baseType = null;
        TypeNode[] interfaces = null;
        var bounds = new Dictionary<int, TypeNode[]>();
        var parts = new List<Part>();
        while (inner.Read() && inner.TokenType != JsonTokenType.EndObject)
        {
            if (inner.TokenType != JsonTokenType.PropertyName) throw new FormatException();
            var part = inner.GetString() switch {
                "base" => Part.Base, "interfaces" => Part.Interfaces, "bounds" => Part.Bounds,
                _ => throw new FormatException(),
            };
            if (parts.Contains(part) || !inner.Read()) throw new FormatException();
            parts.Add(part);
            switch (part)
            {
                case Part.Base: baseType = TypeNode.ReadJson(ref inner); break;
                case Part.Interfaces: interfaces = Types(ref inner); break;
                case Part.Bounds:
                    if (inner.TokenType != JsonTokenType.StartObject) throw new FormatException();
                    while (inner.Read() && inner.TokenType != JsonTokenType.EndObject)
                    {
                        if (inner.TokenType != JsonTokenType.PropertyName) throw new FormatException();
                        var index = int.Parse(inner.GetString(), CultureInfo.InvariantCulture);
                        if (!inner.Read() || !bounds.TryAdd(index, Types(ref inner))) throw new FormatException();
                    }
                    if (inner.TokenType != JsonTokenType.EndObject) throw new FormatException();
                    break;
            }
        }
        if (inner.TokenType != JsonTokenType.EndObject || inner.Read()) throw new FormatException();
        return new SourceTypeFacts(baseType, interfaces, bounds, parts);
    }

    static TypeNode[] Types(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new FormatException();
        var types = new List<TypeNode>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) types.Add(TypeNode.ReadJson(ref reader));
        if (reader.TokenType != JsonTokenType.EndArray) throw new FormatException();
        return types.ToArray();
    }

    string ToJson()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = BirJson.MaxDepth }))
        {
            writer.WriteStartObject();
            // Keep insertion order at this frozen-text boundary; it is transport
            // bookkeeping, not dynamic property-based semantic dispatch.
            foreach (var part in order)
                switch (part)
                {
                    case Part.Base:
                        writer.WritePropertyName("base"); TypeNode.WriteJson(writer, Base); break;
                    case Part.Interfaces:
                        writer.WriteStartArray("interfaces");
                        foreach (var edge in Interfaces) TypeNode.WriteJson(writer, edge);
                        writer.WriteEndArray(); break;
                    case Part.Bounds:
                        writer.WriteStartObject("bounds");
                        foreach (var (index, constraints) in Bounds)
                        {
                            writer.WriteStartArray(index.ToString(CultureInfo.InvariantCulture));
                            foreach (var constraint in constraints) TypeNode.WriteJson(writer, constraint);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndObject(); break;
                }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static void Write(Utf8JsonWriter writer, SourceTypeFacts value) => writer.WriteStringValue(value.ToJson());
    internal static JsonNode ToDom(SourceTypeFacts value) => JsonValue.Create(value.ToJson());

    internal static void SelfTest()
    {
        var original = new TypeNode[] { new TypeNode.Fqn("Witness.日本", new TypeNode[] { new TypeNode.Tv("method", 1) }) };
        var snapshot = new SourceTypeFacts(new Dictionary<int, TypeNode[]> { [0] = original });
        var merged = snapshot.Merge(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Fqn("Later") },
            [3] = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("type", 2)) },
        });
        original[0] = new TypeNode.Fqn("MutatedInput");
        if (snapshot.Bounds.Count != 1 || merged.Bounds.Count != 2
            || snapshot.Bounds[0][0] is not TypeNode.Fqn { Name: "Witness.日本" }
            || merged.Bounds[0][0] != snapshot.Bounds[0][0])
            throw new InvalidOperationException("Source type facts lost input isolation or earliest source bounds");
        var first = new TypeNode.Fqn("First");
        var firstEdge = new TypeNode.Fqn("Contract", new TypeNode[] { first });
        var edges = new TypeNode[] { firstEdge };
        merged = merged.Merge(first, edges, null).Merge(new TypeNode.Fqn("Later"), new TypeNode[] {
            new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Fqn("Later") }), new TypeNode.Fqn("Other"),
        }, null);
        edges[0] = new TypeNode.Fqn("MutatedInput");
        if (merged.Base != first || merged.Interfaces.Count != 2 || merged.Interfaces[0] != firstEdge
            || snapshot.Base != null || snapshot.Interfaces != null)
            throw new InvalidOperationException("Source type facts lost earliest edges or caller ownership");
        var bytes = Encoding.UTF8.GetBytes(ToDom(merged).ToJsonString(BirJson.Writer));
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read()) throw new InvalidOperationException();
        var roundtrip = Read(ref reader);
        if (reader.Read() || ToDom(roundtrip).GetValue<string>() != ToDom(merged).GetValue<string>()
            || roundtrip.Bounds[3].Single() != new TypeNode.Nullable(new TypeNode.Tv("type", 2)))
            throw new InvalidOperationException("Source type facts changed frames, presence or Unicode at the frontier");
        Console.WriteLine("[source type facts] self-test OK (typed edges/bounds, first-writer wins, input isolation, exact leaf codec)");
    }
}
