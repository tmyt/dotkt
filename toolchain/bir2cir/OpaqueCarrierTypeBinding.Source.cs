using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OpaqueCarrierTypeBinding
{
    readonly record struct PhysicalDeclaration(string Name, string NestedIn, int OwnArity);

    // Both owning readers supply selected placement facts. There is no second
    // nesting resolver or inference from a synthetic declaration's spelling.
    static IReadOnlyDictionary<string, string> PlanPhysicalNames(IEnumerable<PhysicalDeclaration> occurrences,
        IReadOnlySet<string> fileClasses)
    {
        var declarations = new Dictionary<string, PhysicalDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in occurrences)
            if (!declarations.TryAdd(declaration.Name, declaration)
                && declarations[declaration.Name] != declaration)
                throw new InvalidOperationException(
                    $"conflicting Kotlin type declaration identity '{declaration.Name}' in opaque carrier binding");
        var physical = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        string Physical(string name)
        {
            if (fileClasses.Contains(name)) return name;
            if (physical.TryGetValue(name, out var cached)) return cached;
            if (!declarations.TryGetValue(name, out var declaration)) return name;
            if (!active.Add(name)) throw new InvalidOperationException($"cyclic Kotlin semantic type ownership at '{name}'");
            try
            {
                if (declaration.NestedIn is not string owner)
                    return physical[name] = declaration.OwnArity > 0 && !name.Contains('`') ? name + "`" + declaration.OwnArity : name;
                // NestedIn selects the actual parent; only the TypeDef's leaf
                // name comes from its last dotted segment, exactly as in ilemit.
                var simple = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;
                if (declaration.OwnArity > 0 && !simple.Contains('`')) simple += "`" + declaration.OwnArity;
                return physical[name] = Physical(owner) + "+" + simple;
            }
            finally { active.Remove(name); }
        }
        foreach (var declaration in declarations.Values)
            if (declaration.NestedIn != null) _ = Physical(declaration.Name);
        return physical.Where(pair => declarations[pair.Key].NestedIn != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        SourceDeclarationSignatures semanticSignatures = null)
    {
        var facades = roots.Where(root => root.FileClass.HasValue).Select(root => root.FileClass.Value)
            .ToHashSet(StringComparer.Ordinal);
        var physical = PlanPhysicalNames(roots.SelectMany(root => root.Types.OrEmpty())
            .Where(type => type.Name.HasValue)
            .Select(type => new PhysicalDeclaration(type.Name.Value, type.NestedIn.OrDefault(), type.TypeParams.OrEmpty().Length)), facades);
        TypeNode Source(TypeNode type) => SourceDeclarationSignature.RewriteType(type,
            classifier => BindSourceClassifier(classifier, physical, refs));
        TypeNode Local(TypeNode type) => SourceDeclarationSignature.RewriteType(type,
            classifier => physical.TryGetValue(classifier.Name, out var exact) ? classifier with { Name = exact } : classifier);

        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirFunctionDeclaration method)
            {
                if (physical.Count > 0 && method.InlineBir.HasValue && method.InlineBir.Value.Length > 0)
                {
                    var payload = ExecutableBirCodec.ReadInline(ExecutableBirCodec.Codec, method.InlineBir.Value);
                    BindExecutablePayload(payload, physical);
                    method.InlineBir = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, payload);
                }
                if (physical.Count > 0) method.SuspendResult = Map(method.SuspendResult, Local);
            }
            if (node is BirAttribute attribute && attribute.Attr.OrDefault() is TypeNode.Fqn { Name: DefaultBirCarrier.AttributeName })
                DefaultBirCarrier.Rewrite(attribute, expression => {
                    if (physical.Count > 0) BindExecutablePayload((BirGraphValue)expression, physical);
                    MaterializedBirPayload.HideSourceSpans((BirGraphValue)expression);
                    return expression;
                });
            RewriteSourceRecords(node, Source);
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
        semanticSignatures?.RewriteClassifiers(classifier => BindSourceClassifier(classifier, physical, refs));
    }

    static OptionalField<T> Map<T>(OptionalField<T> fact, Func<T, T> rewrite) => fact.HasValue
        ? OptionalField<T>.Of(rewrite(fact.Value)) : fact;

    // These exact frozen leaf properties are this phase's authored metadata
    // obligation. Ordinary Type/Ret/Sty and other semantic facts are not slots
    // of this rewrite, even when they contain the same classifier.
    static void RewriteSourceRecords(BirGraphValue node, Func<TypeNode, TypeNode> rewrite)
    {
        SourceTypeFacts Types(SourceTypeFacts value) => value.RewriteTypes(rewrite);
        SourceBoundsSnapshot Bounds(SourceBoundsSnapshot value) => value.RewriteTypes(rewrite);
        switch (node)
        {
            case BirClassDefinition value: value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types); break;
            case BirInterfaceDefinition value: value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types); break;
            case BirEnumDefinition value: value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types); break;
            case BirRefTypeDeclaration value:
                value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types);
                value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds); break;
            case BirInheritedImplementation value:
                value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types);
                value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds); break;
            case BirClosureDefinition value:
                value.KotlinSupertypesPre = Map(value.KotlinSupertypesPre, Types);
                value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds);
                value.NullableGenericRet = Map(value.NullableGenericRet, rewrite); break;
            case BirFunctionDeclaration value:
                value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds);
                value.NullableGenericRet = Map(value.NullableGenericRet, rewrite); break;
            case BirInheritedClassMethod value:
                value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds);
                value.NullableGenericRet = Map(value.NullableGenericRet, rewrite); break;
            case BirMethodImpl value: value.MethodTypeParameterBoundsPre = Map(value.MethodTypeParameterBoundsPre, Bounds); break;
            case BirInheritedDefaultMethod value: value.NullableGenericRet = Map(value.NullableGenericRet, rewrite); break;
            case BirInheritedDefaultAccessor value: value.NullableGenericRet = Map(value.NullableGenericRet, rewrite); break;
            case BirParameter value: value.NullableGeneric = Map(value.NullableGeneric, rewrite); break;
            case BirFieldDeclaration value: value.NullableGeneric = Map(value.NullableGeneric, rewrite); break;
            case BirPropertyDeclaration value: value.NullableGeneric = Map(value.NullableGeneric, rewrite); break;
            case BirCaptureDescriptor value: value.NullableGeneric = Map(value.NullableGeneric, rewrite); break;
            case BirLambdaParameter value: value.NullableGeneric = Map(value.NullableGeneric, rewrite); break;
        }
    }
}
