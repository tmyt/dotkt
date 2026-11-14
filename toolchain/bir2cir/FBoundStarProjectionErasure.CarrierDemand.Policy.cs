using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    sealed record ConstructionDeclaration(string Kind, TypeNode.Fqn Base, TypeNode.Fqn[] Interfaces);

    static bool TryVarianceProjectedArrayOwner(TypeNode element, IReadOnlyList<(bool Null, TypeNode Type)> elements,
        CarrierTypeContext context, out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (StripSourceNullability(element) is not TypeNode.Fqn { Args: { Length: > 0 } } target || elements.Count == 0) return false;
        var targetKey = string.Join("\0", target.Args.Select(SupertypeGraph.TypeKey));
        var sawTypedElement = false;
        var incompatible = false;
        foreach (var value in elements)
        {
            if (value.Null) continue;
            if (StripSourceNullability(value.Type) is not TypeNode.Fqn source) return false;
            var projected = SameDeclarationOwner(source.Name, target.Name) ? source.Args ?? Array.Empty<TypeNode>()
                : ProjectConstructedArguments(source, target.Name, context)?.ToArray();
            if (projected == null || projected.Length != target.Args.Length) return false;
            sawTypedElement = true;
            if (string.Join("\0", projected.Select(SupertypeGraph.TypeKey)) != targetKey
                && !CanUseClrVariance(target, projected, context)) incompatible = true;
        }
        if (!sawTypedElement || !incompatible) return false;
        targetOwner = target;
        return true;
    }

    static bool RequiresExistentialConversion(TypeNode sourceType, TypeNode.Fqn target, CarrierTypeContext context)
    {
        if (StripSourceNullability(sourceType) is not TypeNode.Fqn source) return false;
        var projected = SameDeclarationOwner(source.Name, target.Name) ? source.Args ?? Array.Empty<TypeNode>()
            : ProjectConstructedArguments(source, target.Name, context)?.ToArray();
        if (projected == null || projected.Length != target.Args.Length) return false;
        return string.Join("\0", projected.Select(SupertypeGraph.TypeKey)) != string.Join("\0", target.Args.Select(SupertypeGraph.TypeKey))
            && !CanUseClrVariance(target, projected, context);
    }

    // Narrow declaration readers, not a second semantic catalog. Both graph
    // owners use the same nearest-owner, assignability and variance policies.
    sealed class CarrierTypeContext(
        Func<string, ConstructionDeclaration> definition,
        Func<string, ClrVarianceFacts.Declaration> variance,
        ReferenceMetadataIndex references)
    {
        internal ReferenceMetadataIndex References => references;
        internal ConstructionDeclaration Definition(string name) => definition(name);
        internal string Variance(string name, int arity, int index) => new ClrTypeVariance.Analysis(variance, references).Get(name, arity, index);
        internal static CarrierTypeContext ForDom(IReadOnlyDictionary<string, JsonObject> definitions, ReferenceMetadataIndex refs) => new(
            name => definitions.TryGetValue(name, out var type) ? new(Str(type["kind"]), TypeJson.Read(type["base"]) as TypeNode.Fqn,
                (type["interfaces"] as JsonArray)?.Select(TypeJson.Read).OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>()) : null,
            name => definitions.TryGetValue(name, out var type) ? ClrVarianceFacts.FromDom(type) : null, refs);
        internal static CarrierTypeContext ForSource(CurrentDeclarationPreparation preparation, ReferenceMetadataIndex refs) => new(
            name => preparation.Bindings.SourceDefinitions.TryGetValue(name, out var binding)
                && binding.Source is IBirTypeDeclaration type ? new(type.Kind.OrDefault(), type.Base.OrDefault() as TypeNode.Fqn,
                    type.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray()) : null,
            name => preparation.Bindings.SourceDefinitions.TryGetValue(name, out var binding)
                && binding.Source is IBirTypeDeclaration type ? ClrVarianceFacts.FromSource(type) : null, refs);
    }

    static bool CanUseClrVariance(TypeNode.Fqn target, IReadOnlyList<TypeNode> sourceArguments,
        CarrierTypeContext context, bool physicalTypes = false)
    {
        if (target.Args == null || sourceArguments.Count != target.Args.Length) return false;
        for (var index = 0; index < target.Args.Length; index++)
        {
            if (SupertypeGraph.TypeKey(sourceArguments[index]) == SupertypeGraph.TypeKey(target.Args[index]))
                continue;
            if (!TryClrVariance(target.Name, target.Args.Length, index, context, out var variance)
                || variance is not ("in" or "out")
                || !(variance == "out"
                    ? IsClrAssignable(sourceArguments[index], target.Args[index], context, physicalTypes)
                    : IsClrAssignable(target.Args[index], sourceArguments[index], context, physicalTypes)))
                return false;
        }
        return true;
    }

    static bool TryClrVariance(string ownerName, int ownerArity, int index,
        CarrierTypeContext context, out string variance)
    {
        variance = context.Variance(ownerName, ownerArity, index);
        return variance != null;
    }

    static bool IsClrAssignable(TypeNode source, TypeNode target,
        CarrierTypeContext context, bool physicalTypes = false,
        bool allowBoxing = false)
    {
        var refs = context.References;
        source = StripSourceNullability(source);
        target = StripSourceNullability(target);
        if (SupertypeGraph.TypeKey(source) == SupertypeGraph.TypeKey(target)) return true;
        // Boxing is an optional ROOT value conversion, never an allowance inside CLR generic variance or arrays.
        if (!(IsDefinitelyClrReferenceType(source, context, physicalTypes)
                || allowBoxing && IsBoxableValue(source, context, physicalTypes))
            || !IsDefinitelyClrReferenceType(target, context, physicalTypes)) return false;
        if (target is TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "System.Object" or "object" })
            return true;
        if (source is TypeNode.Array sourceArray && target is TypeNode.Array targetArray)
            return sourceArray.Rank == targetArray.Rank && sourceArray.SzArray == targetArray.SzArray
                && IsClrAssignable(sourceArray.Elem, targetArray.Elem, context, physicalTypes);
        if (source is not TypeNode.Fqn sourceFqn || target is not TypeNode.Fqn targetFqn) return false;

        var reflectedSource = ResolveClosedReferenceType(sourceFqn, refs, physicalTypes);
        var reflectedTarget = ResolveClosedReferenceType(targetFqn, refs, physicalTypes);
        if (reflectedSource != null && reflectedTarget != null)
            return reflectedTarget.IsAssignableFrom(reflectedSource);

        // A semantic edge out of an alias does not prove a direct CLR conversion to a locally emitted type.
        // `kotlin.String : CharSequence` is the canonical case: System.String does not implement the synthetic
        // dotkt$CharSequence interface and needs an adapter. Reflection above remains authoritative when both
        // endpoints are external; a local target cannot be inferred from the Kotlin hierarchy of an aliased source.
        if (reflectedSource != null && reflectedTarget == null
            && refs.TryResolveClrOwner(sourceFqn.Name, out _, out _))
            return false;

        var projected = ProjectConstructedArguments(sourceFqn, targetFqn.Name, context);
        if (projected == null) return false;
        if (targetFqn.Args == null) return true;
        return projected.Count == targetFqn.Args.Length
            && CanUseClrVariance(targetFqn, projected, context, physicalTypes);
    }

    static bool IsBoxableValue(TypeNode type, CarrierTypeContext context, bool physicalTypes)
    {
        var refs = context.References;
        if (type is not TypeNode.Fqn named || refs.IsByRefLikeFqn(named)) return false;
        if (context.Definition(named.Name) is { } local) return local.Kind is "struct" or "enum";
        var reflected = ResolveClosedReferenceType(named, refs, physicalTypes);
        return reflected is { IsValueType: true, IsByRefLike: false } && reflected.FullName != "System.Void";
    }

    static bool IsDefinitelyClrReferenceType(TypeNode type,
        CarrierTypeContext context, bool physicalTypes = false) => type switch
    {
        TypeNode.Nullable nullable => nullable.Of is TypeNode.Fqn valueFqn
            ? !context.References.IsValueType(valueFqn) && IsDefinitelyClrReferenceType(nullable.Of, context, physicalTypes)
            : IsDefinitelyClrReferenceType(nullable.Of, context, physicalTypes),
        TypeNode.Oblivious oblivious => IsDefinitelyClrReferenceType(oblivious.Of, context, physicalTypes),
        TypeNode.Array or TypeNode.Fn => true,
        TypeNode.Fqn f when context.Definition(f.Name) is { } local =>
            local.Kind is not ("struct" or "enum"),
        TypeNode.Fqn f => (physicalTypes
                ? context.References.PhysicalTypeForProof(f.Name, f.Args?.Length ?? 0)
                : context.References.ResolveNetType(context.References.Aliases.TryGetValue(f.Name, out var alias) ? alias : f.Name,
                    f.Args?.Length ?? 0)) is Type reflected
            && !reflected.IsValueType,
        _ => false,
    };

    static IReadOnlyList<TypeNode> ProjectConstructedArguments(TypeNode.Fqn source, string targetName,
        CarrierTypeContext context)
    {
        var refs = context.References;
        var queue = new Queue<(TypeNode.Fqn Type, int Depth)>();
        queue.Enqueue((source, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        int? matchDepth = null;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (matchDepth is int nearest && depth > nearest) break;
            var currentArguments = current.Args ?? Array.Empty<TypeNode>();
            var key = current.Name + "|[" + string.Join(",", currentArguments.Select(TypeNode.ToJson)) + "]";
            if (!seen.Add(key)) continue;
            if (SameDeclarationOwner(current.Name, targetName))
            {
                matchDepth = depth;
                matches.TryAdd(key, currentArguments);
                continue;
            }

            IEnumerable<TypeNode.Fqn> Edges()
            {
                if (context.Definition(current.Name) is { } definition)
                {
                    if (definition.Base != null) yield return definition.Base;
                    foreach (var edge in definition.Interfaces) yield return edge;
                    yield break;
                }
                foreach (var (spec, _) in refs.ReferencedSupertypes(current)) yield return spec;
            }

            foreach (var edge in Edges())
            {
                if (SubstituteDeclarationTypeArguments(edge, currentArguments, Array.Empty<TypeNode>())
                    is TypeNode.Fqn projected)
                    queue.Enqueue((projected, depth + 1));
            }
        }

        return matches.Count == 1 ? matches.Values.Single() : null;
    }

}
