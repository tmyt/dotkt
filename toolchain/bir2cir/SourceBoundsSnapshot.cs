using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Method bounds have no base/interface edges. Share leaf encoding and merging
// with owner facts, without exposing owner-only facts in a method's typed API.
sealed class SourceBoundsSnapshot
{
    readonly SourceTypeFacts facts;
    internal IReadOnlyDictionary<int, TypeNode[]> Bounds => facts.Bounds;
    internal SourceBoundsSnapshot(IReadOnlyDictionary<int, TypeNode[]> bounds) => facts = new SourceTypeFacts(bounds);
    SourceBoundsSnapshot(SourceTypeFacts facts) => this.facts = facts;
    internal SourceBoundsSnapshot Merge(IReadOnlyDictionary<int, TypeNode[]> additions) => new(facts.Merge(additions));

    internal static SourceBoundsSnapshot Read(ref Utf8JsonReader reader)
        => FromFacts(SourceTypeFacts.Read(ref reader));

    internal static SourceBoundsSnapshot ReadFrozenText(string text) => FromFacts(SourceTypeFacts.ReadFrozenText(text));

    static SourceBoundsSnapshot FromFacts(SourceTypeFacts facts)
    {
        if (!facts.IsBoundsOnly) throw new FormatException();
        return new SourceBoundsSnapshot(facts);
    }

    internal string FrozenText => facts.FrozenText;

    internal static void Write(Utf8JsonWriter writer, SourceBoundsSnapshot value) => SourceTypeFacts.Write(writer, value.facts);
    internal static JsonNode ToDom(SourceBoundsSnapshot value) => SourceTypeFacts.ToDom(value.facts);
}
