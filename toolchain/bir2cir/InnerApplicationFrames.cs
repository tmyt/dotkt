using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Source declaration variables and Kotlin inner applications use distinct orders. The intermediate
// application retains own-first groups, including their companions, until TypeOwnershipLowering.
sealed class InnerApplicationFrames
{
    readonly Dictionary<string, JsonObject> _locals = new(StringComparer.Ordinal);
    readonly ReferenceMetadataIndex _references;
    readonly IReadOnlyDictionary<string, string> _physicalNames;

    public InnerApplicationFrames(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        _physicalNames = references?.PhysicalTypeNames ?? new Dictionary<string, string>();
        void Discover(JsonObject owner)
        {
            if (Text(owner["name"]) is string name) _locals[name] = owner;
            foreach (var child in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Discover(child);
        }
        foreach (var root in roots.OfType<JsonObject>()) Discover(root);
    }

    public Dictionary<string, NullableRepresentationFrame> Project(
        IReadOnlyDictionary<string, NullableRepresentationFrame> declarations, bool retainPhysicalOrder = false,
        string selectedOwner = null)
    {
        var orders = new Dictionary<string, (int[] Sources, int[] Physical)>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        NullableRepresentationFrame Frame(string name)
        {
            if (!_locals.ContainsKey(name)) name = _physicalNames.GetValueOrDefault(name) ?? name;
            if (declarations.TryGetValue(name, out var frame)) return frame;
            if (_locals.TryGetValue(name, out var local))
                return new NullableRepresentationFrame((local["typeParams"] as JsonArray)?.Count ?? 0, Array.Empty<int>());
            if (_references?.TryReferenceTypeShape(new TypeNode.Fqn(name), out var count, out _, out _, out _) == true)
                return new NullableRepresentationFrame(count, Array.Empty<int>());
            throw new InvalidOperationException($"Inner semantic owner '{name}' has no declaration frame");
        }
        (int[] Sources, int[] Physical) Order(string name, NullableRepresentationFrame frame)
        {
            if (orders.TryGetValue(name, out var known)) return known;
            if (!visiting.Add(name)) throw new InvalidOperationException("Cyclic inner application ownership");
            try
            {
                string parent = null;
                var offset = 0;
                if (_locals.TryGetValue(name, out var local))
                {
                    if (local["mods"]?["inner"]?.GetValue<bool>() == true)
                    {
                        parent = Text(local["semanticOwner"]) ?? throw new InvalidOperationException("Inner declaration has no semantic owner");
                        offset = local["outerTypeParamOffset"]?.GetValue<int>() ?? 0;
                    }
                }
                else if (_references?.TryInnerSemanticOwner(name, out var referencedParent) == true)
                    parent = referencedParent;
                var source = Enumerable.Range(0, frame.SourceArity).ToArray();
                var physical = Enumerable.Range(0, frame.PhysicalArity).ToArray();
                if (parent != null)
                {
                    var enclosing = Frame(parent);
                    var parentOrder = Order(parent, enclosing);
                    if (offset < 0 || offset + enclosing.SourceArity > frame.SourceArity)
                        throw new InvalidOperationException("Inner source capture exceeds declaration frame");
                    source = source.Where(index => index < offset || index >= offset + enclosing.SourceArity)
                        .Concat(parentOrder.Sources.Select(index => offset + index)).ToArray();
                    var captured = parentOrder.Physical.Select(index => {
                        var slot = enclosing.PhysicalSlot(index);
                        return frame.Variable(new TypeNode.Tv("type", offset + slot.SourceIndex), slot.Representation).I;
                    }).ToArray();
                    var capturedSet = captured.ToHashSet();
                    physical = physical.Where(index => !capturedSet.Contains(index)).Concat(captured).ToArray();
                }
                return orders[name] = (source, physical);
            }
            finally { visiting.Remove(name); }
        }
        var selected = selectedOwner == null ? declarations.AsEnumerable()
            : new[] { new KeyValuePair<string, NullableRepresentationFrame>(selectedOwner, declarations[selectedOwner]) };
        return selected.ToDictionary(pair => pair.Key, pair => {
            var order = Order(pair.Key, pair.Value);
            return pair.Value.ForApplication(order.Sources, retainPhysicalOrder
                ? Enumerable.Range(0, pair.Value.PhysicalArity).ToArray() : order.Physical);
        }, StringComparer.Ordinal);
    }

    static string Text(JsonNode node) => node?.GetValue<string>();
}
