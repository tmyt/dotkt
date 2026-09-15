#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

// A declaration-owned correspondence, independent of parameter names or Kotlin reified markers.
// Source parameters keep their indices. Demanded nullable representations follow in source-index order.
internal sealed class NullableRepresentationFrame
{
    public int SourceArity { get; }
    public IReadOnlyList<int> NullableIndices { get; }
    public int PhysicalArity => SourceArity + NullableIndices.Count;

    public NullableRepresentationFrame(int sourceArity, IEnumerable<int> nullableIndices)
    {
        var indices = nullableIndices.ToArray();
        if (sourceArity < 0 || indices.Any(i => i < 0 || i >= sourceArity)
            || !indices.SequenceEqual(indices.Distinct().OrderBy(i => i)))
            throw new ArgumentException("Invalid nullable representation frame");
        SourceArity = sourceArity;
        NullableIndices = Array.AsReadOnly(indices);
    }

    public TypeNode.Tv NullableVariable(TypeNode.Tv source)
    {
        if (source.Scope is not ("type" or "method"))
            throw new ArgumentException("Invalid generic parameter scope");
        for (var index = 0; index < NullableIndices.Count; index++)
            if (NullableIndices[index] == source.I)
                return new TypeNode.Tv(source.Scope, SourceArity + index);
        throw new InvalidOperationException("Nullable representation was not demanded by this declaration");
    }

    // Both mappings receive the SOURCE argument. Applying the nullable mapping to an already-erased argument
    // would lose precisely the distinction this frame exists to preserve.
    public TypeNode[] Close(IReadOnlyList<TypeNode> sourceArguments,
        Func<TypeNode, TypeNode> ordinary, Func<TypeNode, TypeNode> nullable)
    {
        if (sourceArguments.Count != SourceArity)
            throw new ArgumentException("Source generic arity does not match nullable representation frame");
        return sourceArguments.Select(ordinary)
            .Concat(NullableIndices.Select(index => nullable(sourceArguments[index]))).ToArray();
    }

    public TypeNode SemanticVariable(TypeNode.Tv physical)
    {
        if (physical.Scope is not ("type" or "method") || physical.I < 0 || physical.I >= PhysicalArity)
            throw new ArgumentException("Physical generic variable does not match nullable representation frame");
        return physical.I < SourceArity ? physical
            : new TypeNode.Nullable(new TypeNode.Tv(physical.Scope, NullableIndices[physical.I - SourceArity]));
    }

    // Drops only the frame's added arguments. The retained physical arguments still require the ordinary
    // Kotlin metadata projection; this operation cannot recover source types erased by another representation.
    public TypeNode[] OrdinaryArguments(IReadOnlyList<TypeNode> physicalArguments)
    {
        if (physicalArguments.Count != PhysicalArity)
            throw new ArgumentException("Physical generic arity does not match nullable representation frame");
        return physicalArguments.Take(SourceArity).ToArray();
    }

    public JsonObject ToJson() => new()
    {
        ["sourceArity"] = SourceArity,
        ["nullable"] = new JsonArray(NullableIndices.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
    };

    public static NullableRepresentationFrame Read(JsonNode node)
    {
        if (node is not JsonObject obj || obj.Count != 2
            || obj["sourceArity"] is not JsonValue arityNode || !arityNode.TryGetValue<int>(out var arity)
            || obj["nullable"] is not JsonArray indices)
            throw new ArgumentException("Malformed nullable representation frame");
        return new NullableRepresentationFrame(arity, indices.Select(item =>
            item is JsonValue value && value.TryGetValue<int>(out var index) ? index
                : throw new ArgumentException("Malformed nullable representation index")));
    }
}
