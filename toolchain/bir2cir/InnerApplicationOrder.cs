using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Reorder an application's own/enclosing segments without changing the types carried in those slots.
// Declaration-relative substitution needs this order even before the application itself is projected.
sealed class InnerApplicationOrder
{
    readonly Dictionary<string, (int Count, string Owner)> _locals = new(StringComparer.Ordinal);
    readonly ReferenceMetadataIndex _references;

    public InnerApplicationOrder(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references)
    {
        _references = references;
        foreach (var root in roots) Add(root);
    }

    public void Add(JsonNode node)
    {
        if (node is JsonObject owner)
        {
            if (owner["mods"]?["inner"]?.GetValue<bool>() == true
                && owner["outerTypeParamCount"]?.GetValue<int>() is int count && count > 0
                && owner["name"]?.GetValue<string>() is string name)
                _locals[name] = (count, owner["semanticOwner"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"Kotlin inner type '{name}' has no semantic owner"));
            foreach (var child in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Add(child);
        }
    }

    public TypeNode[] DeclarationArguments(TypeNode.Fqn application)
    {
        if (application?.Args is not { } arguments) return null;
        var found = _locals.TryGetValue(application.Name, out var shape);
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
