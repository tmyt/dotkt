using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Reorder an application's own/enclosing segments without changing the types carried in those slots.
// Declaration-relative substitution needs this order even before the application itself is projected.
sealed class InnerApplicationOrder
{
    internal readonly record struct DeclarationFact(string Name, bool Inner, int CapturedCount, string SemanticOwner);
    sealed record Shape(int Version, int Count, string Owner, Shape Previous);
    readonly Dictionary<string, Shape> _locals = new(StringComparer.Ordinal);
    readonly ReferenceMetadataIndex _references;
    readonly int? _visibleThrough;
    int _version;

    InnerApplicationOrder(InnerApplicationOrder source)
    {
        _references = source._references;
        _locals = source._locals;
        _visibleThrough = source._visibleThrough ?? source._version;
    }

    // Declaration shape facts are shared. Only a genuinely changed local shape
    // adds a scalar history link; no dictionary or declaration is cloned.
    internal InnerApplicationOrder CaptureView() => new(this);

    public InnerApplicationOrder(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        foreach (var root in roots) Add(root);
    }

    internal InnerApplicationOrder(IEnumerable<BirFileDeclaration> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        foreach (var root in roots) Add(Capture(root));
    }

    internal static DeclarationFact[] Capture(BirFileDeclaration file)
        => file.Types.OrEmpty().OfType<BirClassDefinition>().Select(type => new DeclarationFact(
            type.Name.OrDefault(), type.Mods.OrDefault()?.Inner.OrDefault() == true,
            type.OuterTypeParamCount.OrDefault(), type.SemanticOwner.OrDefault())).ToArray();

    internal void Add(IEnumerable<DeclarationFact> declarations)
    {
        foreach (var fact in declarations) Add(fact.Name, fact.Inner, fact.CapturedCount, fact.SemanticOwner);
    }

    public void Add(JsonNode node)
    {
        if (node is JsonObject owner)
        {
            Add(owner["name"]?.GetValue<string>(), owner["mods"]?["inner"]?.GetValue<bool>() == true,
                owner["outerTypeParamCount"]?.GetValue<int>() ?? 0, owner["semanticOwner"]?.GetValue<string>());
            foreach (var child in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Add(child);
        }
    }

    void Add(string name, bool inner, int count, string semanticOwner)
    {
        if (_visibleThrough != null)
            throw new InvalidOperationException("An inner-application visibility view cannot admit declarations");
        if (inner && count > 0 && name != null)
        {
            var owner = semanticOwner
                ?? throw new InvalidOperationException($"Kotlin inner type '{name}' has no semantic owner");
            _locals.TryGetValue(name, out var prior);
            if (prior != null && prior.Count == count && prior.Owner == owner) return;
            _locals[name] = new Shape(++_version, count, owner, prior);
        }
    }

    public TypeNode[] DeclarationArguments(TypeNode.Fqn application)
    {
        if (application?.Args is not { } arguments) return null;
        _locals.TryGetValue(application.Name, out var local);
        while (local != null && _visibleThrough is { } through && local.Version > through) local = local.Previous;
        var found = local != null;
        var shape = found ? (local.Count, local.Owner) : default;
        if (!found && _references?.TryInnerCapturedCount(application.Name, out var count) == true)
        {
            if (!_references.TryInnerSemanticOwner(application.Name, out var owner))
                throw new InvalidOperationException($"Referenced Kotlin inner type '{application.Name}' has no semantic owner fact");
            shape = (count, owner);
            found = true;
        }
        if (!found || shape.Count == 0) return arguments;
        if (shape.Count > arguments.Length)
            throw new InvalidOperationException($"Kotlin inner application '{application.Name}' supplies {arguments.Length} arguments for {shape.Count} captured slots");
        var ownCount = arguments.Length - shape.Count;
        var enclosing = DeclarationArguments(new TypeNode.Fqn(shape.Owner, arguments.Skip(ownCount).ToArray()));
        return enclosing.Concat(arguments.Take(ownCount)).ToArray();
    }
}
