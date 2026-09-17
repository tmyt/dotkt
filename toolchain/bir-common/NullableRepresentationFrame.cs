#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

// A declaration-owned correspondence, independent of parameter names or Kotlin reified markers.
// The canonical frame is source parameters followed by nullable, storage and nullable-storage companions.
// Each companion describes a representation of the same SOURCE argument, not a new source type parameter.
// PhysicalOrder maps physical slots
// to that canonical frame, allowing a nested CLR type to retain its complete enclosing-type prefix.
internal sealed class NullableRepresentationFrame
{
    public const string MetadataKey = "nullableFrame";
    public int SourceArity { get; }
    public IReadOnlyList<int> NullableIndices { get; }
    public IReadOnlyList<int> StorageIndices { get; }
    public IReadOnlyList<int> NullableStorageIndices { get; }
    public IReadOnlyList<int> PhysicalOrder { get; }
    readonly int[] _canonicalToPhysical;
    readonly Slot[] _canonical;
    public int PhysicalArity => _canonical.Length;
    public bool RequiresMetadata => PhysicalArity != SourceArity || PhysicalOrder.Where((slot, index) => slot != index).Any();
    public IEnumerable<Slot> Companions => _canonical.Skip(SourceArity);

    public enum Role { Ordinary, Nullable, Storage, NullableStorage }
    public readonly record struct Slot(int SourceIndex, Role Representation);

    public NullableRepresentationFrame(int sourceArity, IEnumerable<int> nullableIndices,
        IEnumerable<int>? physicalOrder = null, IEnumerable<int>? storageIndices = null,
        IEnumerable<int>? nullableStorageIndices = null)
    {
        if (sourceArity < 0) throw new ArgumentException("Invalid representation source arity");
        static IReadOnlyList<int> Indices(IEnumerable<int> values, int arity)
        {
            var indices = values.ToArray();
            if (indices.Any(i => i < 0 || i >= arity)
                || !indices.SequenceEqual(indices.Distinct().OrderBy(i => i)))
                throw new ArgumentException("Invalid representation companion indices");
            return Array.AsReadOnly(indices);
        }
        SourceArity = sourceArity;
        NullableIndices = Indices(nullableIndices, sourceArity);
        StorageIndices = Indices(storageIndices ?? Array.Empty<int>(), sourceArity);
        NullableStorageIndices = Indices(nullableStorageIndices ?? Array.Empty<int>(), sourceArity);
        _canonical = Enumerable.Range(0, sourceArity).Select(i => new Slot(i, Role.Ordinary))
            .Concat(NullableIndices.Select(i => new Slot(i, Role.Nullable)))
            .Concat(StorageIndices.Select(i => new Slot(i, Role.Storage)))
            .Concat(NullableStorageIndices.Select(i => new Slot(i, Role.NullableStorage))).ToArray();
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

    public Slot PhysicalSlot(int physicalIndex) => physicalIndex >= 0 && physicalIndex < PhysicalArity
        ? _canonical[PhysicalOrder[physicalIndex]] : throw new ArgumentException("Invalid physical generic index");

    public TypeNode.Tv NullableVariable(TypeNode.Tv source) => Variable(source, Role.Nullable);

    public TypeNode.Tv Variable(TypeNode.Tv source, Role representation)
    {
        if (source.Scope is not ("type" or "method"))
            throw new ArgumentException("Invalid generic parameter scope");
        var index = Array.IndexOf(_canonical, new Slot(source.I, representation));
        if (index < 0) throw new InvalidOperationException(
            $"Representation {representation} of {source.Scope}[{source.I}] was not demanded by frame of source arity {SourceArity}");
        return new TypeNode.Tv(source.Scope, _canonicalToPhysical[index]);
    }

    // All mappings receive the SOURCE argument. Applying a companion mapping to an already-erased argument
    // would lose precisely the distinction this frame exists to preserve.
    public TypeNode[] Close(IReadOnlyList<TypeNode> sourceArguments,
        Func<TypeNode, TypeNode> ordinary, Func<TypeNode, TypeNode> nullable,
        Func<TypeNode, TypeNode>? storage = null, Func<TypeNode, TypeNode>? nullableStorage = null)
    {
        if (sourceArguments.Count != SourceArity)
            throw new ArgumentException("Source generic arity does not match nullable representation frame");
        if (StorageIndices.Count != 0 && storage == null || NullableStorageIndices.Count != 0 && nullableStorage == null)
            throw new ArgumentException("Missing demanded storage representation mapping");
        var canonical = _canonical.Select(slot => (slot.Representation switch {
            Role.Ordinary => ordinary,
            Role.Nullable => nullable,
            Role.Storage => storage!,
            Role.NullableStorage => nullableStorage!,
            _ => throw new InvalidOperationException("Invalid representation role"),
        })(sourceArguments[slot.SourceIndex])).ToArray();
        return PhysicalOrder.Select(index => canonical[index]).ToArray();
    }

    public NullableRepresentationFrame WithEnclosingPrefix(NullableRepresentationFrame enclosing, int sourceOffset)
    {
        if (sourceOffset < 0 || sourceOffset + enclosing.SourceArity > SourceArity)
            throw new ArgumentException("Enclosing source frame does not fit the captured frame");
        var prefix = enclosing.PhysicalOrder.Select(index => {
            var slot = enclosing._canonical[index];
            var target = Array.IndexOf(_canonical, new Slot(sourceOffset + slot.SourceIndex, slot.Representation));
            return target >= 0 ? target : throw new ArgumentException("Missing enclosing representation companion");
        }).ToArray();
        var captured = prefix.ToHashSet();
        return new NullableRepresentationFrame(SourceArity, NullableIndices,
            prefix.Concat(PhysicalOrder.Where(index => !captured.Contains(index))), StorageIndices, NullableStorageIndices);
    }

    public NullableRepresentationFrame RetainSources(IReadOnlyList<int> retained)
    {
        if (retained.Any(index => index < 0 || index >= SourceArity) || retained.Distinct().Count() != retained.Count)
            throw new ArgumentException("Invalid retained source indices");
        var renumber = retained.Select((source, index) => (source, index)).ToDictionary(pair => pair.source, pair => pair.index);
        int[] Remap(IReadOnlyList<int> indices) => indices.Where(renumber.ContainsKey)
            .Select(index => renumber[index]).OrderBy(index => index).ToArray();
        var result = new NullableRepresentationFrame(retained.Count, Remap(NullableIndices),
            storageIndices: Remap(StorageIndices), nullableStorageIndices: Remap(NullableStorageIndices));
        var order = PhysicalOrder.Select(index => _canonical[index]).Where(slot => renumber.ContainsKey(slot.SourceIndex))
            .Select(slot => Array.IndexOf(result._canonical, new Slot(renumber[slot.SourceIndex], slot.Representation)));
        return new NullableRepresentationFrame(result.SourceArity, result.NullableIndices, order,
            result.StorageIndices, result.NullableStorageIndices);
    }

    public TypeNode SemanticVariable(TypeNode.Tv physical)
    {
        if (physical.Scope is not ("type" or "method") || physical.I < 0 || physical.I >= PhysicalArity)
            throw new ArgumentException("Physical generic variable does not match nullable representation frame");
        var slot = PhysicalSlot(physical.I);
        var source = new TypeNode.Tv(physical.Scope, slot.SourceIndex);
        return slot.Representation is Role.Nullable or Role.NullableStorage ? new TypeNode.Nullable(source) : source;
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
        ["storage"] = new JsonArray(StorageIndices.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
        ["nullableStorage"] = new JsonArray(NullableStorageIndices.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
        ["order"] = new JsonArray(PhysicalOrder.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
    };

    public static NullableRepresentationFrame Read(JsonNode node)
    {
        if (node is not JsonObject obj || obj.Count != 5
            || obj["sourceArity"] is not JsonValue arityNode || !arityNode.TryGetValue<int>(out var arity)
            || obj["nullable"] is not JsonArray indices || obj["order"] is not JsonArray order
            || obj["storage"] is not JsonArray storage || obj["nullableStorage"] is not JsonArray nullableStorage)
            throw new ArgumentException("Malformed nullable representation frame");
        static IEnumerable<int> ReadIndices(JsonArray items) => items.Select(item =>
            item is JsonValue value && value.TryGetValue<int>(out var index) ? index
                : throw new ArgumentException("Malformed representation index"));
        return new NullableRepresentationFrame(arity, ReadIndices(indices), ReadIndices(order),
            ReadIndices(storage), ReadIndices(nullableStorage));
    }
}
