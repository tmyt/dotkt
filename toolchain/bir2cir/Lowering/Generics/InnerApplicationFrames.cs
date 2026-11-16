using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Source declaration variables and Kotlin inner applications use distinct orders. The intermediate
// application retains own-first groups, including their companions, until TypeOwnershipLowering.
sealed class InnerApplicationFrames
{
    readonly record struct LocalShape(int Arity, bool Inner, string Parent, int Offset);
    readonly Dictionary<string, LocalShape> _locals = new(StringComparer.Ordinal);
    readonly ReferenceMetadataIndex _references;

    public InnerApplicationFrames(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        void Discover(JsonObject owner)
        {
            if (Text(owner["name"]) is string name) _locals[name] = new(
                (owner["typeParams"] as JsonArray)?.Count ?? 0,
                owner["mods"]?["inner"]?.GetValue<bool>() == true,
                Text(owner["semanticOwner"]), owner["outerTypeParamOffset"]?.GetValue<int>() ?? 0);
            foreach (var child in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Discover(child);
        }
        foreach (var root in roots.OfType<JsonObject>()) Discover(root);
    }

    internal InnerApplicationFrames(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            var (name, parameters, modifiers, parent, offset) = declaration switch {
                BirClassDefinition type => (type.Name.OrDefault(), type.TypeParams.OrEmpty().Length,
                    type.Mods.OrDefault(), type.SemanticOwner.OrDefault(), type.OuterTypeParamOffset.OrDefault()),
                BirInterfaceDefinition type => (type.Name.OrDefault(), type.TypeParams.OrEmpty().Length,
                    type.Mods.OrDefault(), type.SemanticOwner.OrDefault(), 0),
                BirEnumDefinition type => (type.Name.OrDefault(), type.TypeParams.OrEmpty().Length,
                    type.Mods.OrDefault(), type.SemanticOwner.OrDefault(), 0),
                _ => throw new InvalidOperationException("Unaccounted inner application owner: " + declaration.GetType().Name),
            };
            if (name != null) _locals[name] = new(parameters, modifiers?.Inner.OrDefault() == true, parent, offset);
        }
    }

    public Dictionary<string, NullableRepresentationFrame> Project(
        IReadOnlyDictionary<string, NullableRepresentationFrame> declarations, bool retainPhysicalOrder = false,
        string selectedOwner = null)
    {
        var orders = new Dictionary<string, (int[] Sources, int[] Physical)>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        NullableRepresentationFrame Frame(string name)
        {
            if (!_locals.ContainsKey(name)) name = _references?.RecordedPhysicalTypeName(name) ?? name;
            if (declarations.TryGetValue(name, out var frame)) return frame;
            if (_locals.TryGetValue(name, out var local))
                return new NullableRepresentationFrame(local.Arity, Array.Empty<int>());
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
                    if (local.Inner)
                    {
                        parent = local.Parent ?? throw new InvalidOperationException("Inner declaration has no semantic owner");
                        offset = local.Offset;
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
