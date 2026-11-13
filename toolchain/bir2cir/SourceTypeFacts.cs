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
    enum Part { Base, Interfaces, Bounds, NullableFrame }
    readonly Part[] order;
    internal static readonly SourceTypeFacts Empty = new(null, null, new Dictionary<int, TypeNode[]>(), Array.Empty<Part>());
    internal TypeNode Base { get; }
    internal IReadOnlyList<TypeNode> Interfaces { get; }
    internal IReadOnlyDictionary<int, TypeNode[]> Bounds { get; }
    internal NullableRepresentationFrame NullableFrame { get; }
    internal bool IsBoundsOnly => order.Length == 1 && order[0] == Part.Bounds;

    internal SourceTypeFacts(IReadOnlyDictionary<int, TypeNode[]> bounds)
        : this(null, null, bounds, new[] { Part.Bounds }) { }

    SourceTypeFacts(TypeNode baseType, IReadOnlyList<TypeNode> interfaces,
        IReadOnlyDictionary<int, TypeNode[]> bounds, IReadOnlyList<Part> order,
        NullableRepresentationFrame nullableFrame = null)
    {
        Base = baseType;
        Interfaces = interfaces?.ToArray();
        Bounds = new ReadOnlyDictionary<int, TypeNode[]>(bounds.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        this.order = order.ToArray();
        NullableFrame = nullableFrame;
    }

    internal SourceTypeFacts Merge(IReadOnlyDictionary<int, TypeNode[]> bounds) => Merge(null, null, bounds);

    internal SourceTypeFacts MergeNullableFrame(NullableRepresentationFrame frame)
    {
        if (NullableFrame is { } prior && (prior.SourceArity != frame.SourceArity
            || !prior.NullableIndices.SequenceEqual(frame.NullableIndices)
            || !prior.StorageIndices.SequenceEqual(frame.StorageIndices)
            || !prior.NullableStorageIndices.SequenceEqual(frame.NullableStorageIndices)
            || !prior.PhysicalOrder.SequenceEqual(frame.PhysicalOrder)))
            throw new InvalidOperationException("Conflicting declaration-owned nullable representation frames");
        return new SourceTypeFacts(Base, Interfaces, Bounds,
            NullableFrame == null ? order.Append(Part.NullableFrame).ToArray() : order, frame);
    }

    // Capturing an owner changes the source-index correspondence itself. Unlike
    // Merge, this deliberately rebases existing facts and replaces their frame.
    internal SourceTypeFacts RebaseCapturedFrame(NullableRepresentationFrame frame, int[] sourceMap, SourceTypeFacts owner)
    {
        TypeNode Remap(TypeNode type) => type switch {
            TypeNode.Tv { Scope: "type" } variable => new TypeNode.Tv("type", sourceMap[variable.I]),
            TypeNode.Fqn value => value with { Args = value.Args?.Select(Remap).ToArray() },
            TypeNode.Nullable value => value with { Of = Remap(value.Of) },
            TypeNode.Oblivious value => value with { Of = Remap(value.Of) },
            TypeNode.Projection value => value with { Of = Remap(value.Of) },
            TypeNode.Array value => value with { Elem = Remap(value.Elem) },
            TypeNode.ByRef value => value with { Of = Remap(value.Of) },
            TypeNode.Ptr value => value with { Of = Remap(value.Of) },
            TypeNode.Mod value => value with { M = Remap(value.M), Of = Remap(value.Of) },
            TypeNode.Fn value => value with { Ret = Remap(value.Ret), Params = value.Params.Select(Remap).ToArray(),
                Recv = value.Recv == null ? null : Remap(value.Recv), Ctx = value.Ctx?.Select(Remap).ToArray() },
            TypeNode.FnPtr value => value with { Ret = Remap(value.Ret), Params = value.Params.Select(Remap).ToArray() },
            _ => type,
        };
        var bounds = Bounds.ToDictionary(pair => sourceMap[pair.Key], pair => pair.Value.Select(Remap).ToArray());
        var parts = order.ToList();
        if (owner != null && order.Contains(Part.Bounds) == false && owner.order.Contains(Part.Bounds)) parts.Add(Part.Bounds);
        if (owner != null) foreach (var pair in owner.Bounds) bounds[pair.Key] = pair.Value;
        if (!parts.Contains(Part.NullableFrame)) parts.Add(Part.NullableFrame);
        return new SourceTypeFacts(Base == null ? null : Remap(Base), Interfaces?.Select(Remap).ToArray(), bounds, parts, frame);
    }

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
        return new SourceTypeFacts(Base ?? baseType, mergedInterfaces, mergedBounds, parts, NullableFrame);
    }

    internal static SourceTypeFacts Read(ref Utf8JsonReader reader)
        => ReadFrozenText(reader.GetString());

    internal static SourceTypeFacts ReadFrozenText(string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        var inner = new Utf8JsonReader(content, new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
        if (!inner.Read() || inner.TokenType != JsonTokenType.StartObject) throw new FormatException();
        TypeNode baseType = null;
        TypeNode[] interfaces = null;
        var bounds = new Dictionary<int, TypeNode[]>();
        NullableRepresentationFrame nullableFrame = null;
        var parts = new List<Part>();
        while (inner.Read() && inner.TokenType != JsonTokenType.EndObject)
        {
            if (inner.TokenType != JsonTokenType.PropertyName) throw new FormatException();
            var part = inner.GetString() switch {
                "base" => Part.Base, "interfaces" => Part.Interfaces, "bounds" => Part.Bounds,
                "nullableFrame" => Part.NullableFrame,
                _ => throw new FormatException(),
            };
            if (parts.Contains(part) || !inner.Read()) throw new FormatException();
            parts.Add(part);
            switch (part)
            {
                case Part.Base: baseType = TypeNode.ReadJson(ref inner); break;
                case Part.Interfaces: interfaces = Types(ref inner); break;
                case Part.NullableFrame: nullableFrame = NullableRepresentationFrame.ReadJson(ref inner); break;
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
        return new SourceTypeFacts(baseType, interfaces, bounds, parts, nullableFrame);
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
                    case Part.NullableFrame:
                        writer.WritePropertyName("nullableFrame"); NullableFrame.WriteJson(writer); break;
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

    internal string FrozenText => ToJson();

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
        var methodBounds = new SourceBoundsSnapshot(snapshot.Bounds).Merge(merged.Bounds);
        var methodReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(SourceBoundsSnapshot.ToDom(methodBounds).ToJsonString(BirJson.Writer)));
        if (!methodReader.Read()) throw new InvalidOperationException();
        var methodRoundtrip = SourceBoundsSnapshot.Read(ref methodReader);
        if (methodReader.Read() || methodRoundtrip.Bounds[0][0] != snapshot.Bounds[0][0]
            || SourceBoundsSnapshot.ToDom(methodRoundtrip).GetValue<string>() != SourceBoundsSnapshot.ToDom(methodBounds).GetValue<string>())
            throw new InvalidOperationException("Method bounds lost their bounds-only contract or earliest source frame");
        const string framed = """{"bounds":{"0":[]},"nullableFrame":{"sourceArity":2,"nullable":[1],"storage":[0],"nullableStorage":[1],"order":[3,0,2,1,4]}}""";
        var saved = ReadFrozenText(framed);
        var extended = saved.Merge(new Dictionary<int, TypeNode[]> { [1] = new[] { new TypeNode.Fqn("System.Object") } });
        if (saved.FrozenText != framed || !ReferenceEquals(saved.NullableFrame, extended.NullableFrame)
            || saved.NullableFrame.SourceArity != 2 || saved.NullableFrame.SourcePosition(0) != 1
            || saved.NullableFrame.SourcePosition(1) != 3
            || !saved.NullableFrame.NullableIndices.SequenceEqual(new[] { 1 })
            || !saved.NullableFrame.StorageIndices.SequenceEqual(new[] { 0 })
            || !saved.NullableFrame.NullableStorageIndices.SequenceEqual(new[] { 1 })
            || !saved.NullableFrame.PhysicalOrder.SequenceEqual(new[] { 3, 0, 2, 1, 4 }))
            throw new InvalidOperationException("Source type facts lost the saved nullable/storage correspondence");
        Console.WriteLine("[source type facts] self-test OK (typed edges/bounds, first-writer wins, input isolation, exact leaf codec, saved nullable/storage frames)");
    }
}
