#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

// A declaration-owned correspondence, independent of parameter names or Kotlin reified markers.
// The canonical frame is source parameters followed by nullable companions. PhysicalOrder maps physical slots
// to that canonical frame, allowing a nested CLR type to retain its complete enclosing-type prefix.
internal sealed class NullableRepresentationFrame
{
    public const string MetadataKey = "nullableFrame";
    public int SourceArity { get; }
    public IReadOnlyList<int> NullableIndices { get; }
    public IReadOnlyList<int> PhysicalOrder { get; }
    readonly int[] _canonicalToPhysical;
    public int PhysicalArity => SourceArity + NullableIndices.Count;

    public NullableRepresentationFrame(int sourceArity, IEnumerable<int> nullableIndices,
        IEnumerable<int>? physicalOrder = null)
    {
        var indices = nullableIndices.ToArray();
        if (sourceArity < 0 || indices.Any(i => i < 0 || i >= sourceArity)
            || !indices.SequenceEqual(indices.Distinct().OrderBy(i => i)))
            throw new ArgumentException("Invalid nullable representation frame");
        SourceArity = sourceArity;
        NullableIndices = Array.AsReadOnly(indices);
        var order = physicalOrder?.ToArray() ?? Enumerable.Range(0, PhysicalArity).ToArray();
        if (!order.OrderBy(i => i).SequenceEqual(Enumerable.Range(0, PhysicalArity)))
            throw new ArgumentException("Invalid nullable representation physical order");
        PhysicalOrder = Array.AsReadOnly(order);
        _canonicalToPhysical = new int[PhysicalArity];
        for (var physical = 0; physical < order.Length; physical++)
            _canonicalToPhysical[order[physical]] = physical;
    }

    public int SourcePosition(int sourceIndex) => sourceIndex >= 0 && sourceIndex < SourceArity
        ? _canonicalToPhysical[sourceIndex] : throw new ArgumentException(
            $"Source generic index {sourceIndex} does not belong to frame of arity {SourceArity}");

    public int? SourceIndex(int physicalIndex) => physicalIndex >= 0 && physicalIndex < PhysicalArity
        ? PhysicalOrder[physicalIndex] < SourceArity ? PhysicalOrder[physicalIndex] : null
        : throw new ArgumentException("Invalid physical generic index");

    public TypeNode.Tv NullableVariable(TypeNode.Tv source)
    {
        if (source.Scope is not ("type" or "method"))
            throw new ArgumentException("Invalid generic parameter scope");
        for (var index = 0; index < NullableIndices.Count; index++)
            if (NullableIndices[index] == source.I)
                return new TypeNode.Tv(source.Scope, _canonicalToPhysical[SourceArity + index]);
        throw new InvalidOperationException($"Nullable representation {source.Scope}[{source.I}] was not demanded by frame "
            + $"(source arity {SourceArity}, nullable indices [{string.Join(",", NullableIndices)}])");
    }

    // Both mappings receive the SOURCE argument. Applying the nullable mapping to an already-erased argument
    // would lose precisely the distinction this frame exists to preserve.
    public TypeNode[] Close(IReadOnlyList<TypeNode> sourceArguments,
        Func<TypeNode, TypeNode> ordinary, Func<TypeNode, TypeNode> nullable)
    {
        if (sourceArguments.Count != SourceArity)
            throw new ArgumentException("Source generic arity does not match nullable representation frame");
        var canonical = sourceArguments.Select(ordinary)
            .Concat(NullableIndices.Select(index => nullable(sourceArguments[index]))).ToArray();
        return PhysicalOrder.Select(index => canonical[index]).ToArray();
    }

    public TypeNode SemanticVariable(TypeNode.Tv physical)
    {
        if (physical.Scope is not ("type" or "method") || physical.I < 0 || physical.I >= PhysicalArity)
            throw new ArgumentException("Physical generic variable does not match nullable representation frame");
        var canonical = PhysicalOrder[physical.I];
        return canonical < SourceArity ? new TypeNode.Tv(physical.Scope, canonical)
            : new TypeNode.Nullable(new TypeNode.Tv(physical.Scope, NullableIndices[canonical - SourceArity]));
    }

    // Drops only the frame's added arguments. The retained physical arguments still require the ordinary
    // Kotlin metadata projection; this operation cannot recover source types erased by another representation.
    public TypeNode[] OrdinaryArguments(IReadOnlyList<TypeNode> physicalArguments)
    {
        if (physicalArguments.Count != PhysicalArity)
            throw new ArgumentException("Physical generic arity does not match nullable representation frame");
        return Enumerable.Range(0, SourceArity).Select(index => physicalArguments[SourcePosition(index)]).ToArray();
    }

    public JsonObject ToJson() => new()
    {
        ["sourceArity"] = SourceArity,
        ["nullable"] = new JsonArray(NullableIndices.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
        ["order"] = new JsonArray(PhysicalOrder.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
    };

    public static NullableRepresentationFrame Read(JsonNode node)
    {
        if (node is not JsonObject obj || obj.Count != 3
            || obj["sourceArity"] is not JsonValue arityNode || !arityNode.TryGetValue<int>(out var arity)
            || obj["nullable"] is not JsonArray indices || obj["order"] is not JsonArray order)
            throw new ArgumentException("Malformed nullable representation frame");
        return new NullableRepresentationFrame(arity, indices.Select(item =>
            item is JsonValue value && value.TryGetValue<int>(out var index) ? index
                : throw new ArgumentException("Malformed nullable representation index")),
            order.Select(item => item is JsonValue value && value.TryGetValue<int>(out var index) ? index
                : throw new ArgumentException("Malformed nullable representation physical order")));
    }
}
