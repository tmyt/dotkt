using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypeOwnershipLowering
{
    // Lexical source paths are scalar declaration facts. Both owning source
    // recording and the remaining DOM entry use this one path-selection rule.
    static Func<string, string> NestedSourceNames(IEnumerable<(string Name, string Owner)> declarations)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, owner) in declarations)
        {
            if (name == null) continue;
            if (!owners.TryAdd(name, owner) && owners[name] != owner)
                throw new InvalidOperationException($"conflicting Kotlin semantic owners for '{name}'");
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string SourceName(string name)
        {
            if (names.TryGetValue(name, out var known)) return known;
            if (!owners.TryGetValue(name, out var owner) || owner == null || !owners.ContainsKey(owner)
                || !name.StartsWith(owner + ".", StringComparison.Ordinal)) return names[name] = name;
            return names[name] = SourceName(owner) + "+" + name[(owner.Length + 1)..];
        }
        return SourceName;
    }

    // Capture descends the complete structured type, including function
    // pointers and modifiers. Projection below deliberately has a narrower
    // rule; metadata capture must not inherit that physical rewrite policy.
    static TypeNode CaptureNestedSourceType(TypeNode type, Func<string, string> sourceName, ReferenceMetadataIndex refs)
    {
        if (type == null) return null;
        var hasNestedClassifier = false;
        var captured = SourceDeclarationSignature.RewriteType(type, classifier => {
            var name = classifier.Name;
            var source = sourceName(name);
            hasNestedClassifier |= (classifier.Classifier != null && !name.Contains('+')) || source != name
                || (refs?.TryInnerCapturedCount(name, out var count) == true && count > 0);
            return classifier.Classifier == null && source != name ? classifier with { Name = source } : classifier;
        });
        return hasNestedClassifier ? captured : null;
    }

    internal static void RecordNestedSourceTypes(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var sourceName = NestedSourceNames(roots.SelectMany(file => file.Types.OrEmpty())
            .Select(type => (type.Name.OrDefault(), type.SemanticOwner.OrDefault())));
        OptionalField<TypeNode> Record(OptionalField<TypeNode> fact, OptionalField<TypeNode> generic, OptionalField<TypeNode> physical)
            => CaptureNestedSourceType(fact.OrDefault() ?? generic.OrDefault() ?? physical.OrDefault(), sourceName, refs) is { } captured
                ? OptionalField<TypeNode>.Of(captured) : fact;
        void Visit(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            // These records own declaration carriers, including the source
            // parameter/capture rows later materialized into real MethodDefs.
            // Attribute named values do not own Kotlin declaration metadata.
            switch (node)
            {
                case BirParameter value when value.Name.HasValue && value.Type.HasValue:
                    value.KotlinType = Record(value.KotlinType, value.NullableGeneric, value.Type); break;
                case BirFieldDeclaration value when value.Name.HasValue && value.Type.HasValue:
                    value.KotlinType = Record(value.KotlinType, value.NullableGeneric, value.Type); break;
                case BirPropertyDeclaration value when value.Name.HasValue && value.Type.HasValue:
                    value.KotlinType = Record(value.KotlinType, value.NullableGeneric, value.Type); break;
                case BirCaptureDescriptor value when value.Name.HasValue && value.Type.HasValue:
                    value.KotlinType = Record(value.KotlinType, value.NullableGeneric, value.Type); break;
                case BirLambdaParameter value when value.Name.HasValue && value.Type.HasValue:
                    value.KotlinType = Record(value.KotlinType, value.NullableGeneric, value.Type); break;
                case BirFunctionDeclaration value when value.Name.HasValue && value.Params.HasValue && value.Ret.HasValue:
                    value.RetKotlinType = Record(value.RetKotlinType, value.NullableGenericRet, value.Ret); break;
            }
            node.VisitChildren(Visit);
        }
        foreach (var root in roots) Visit(root);
    }

    internal static void ProjectInnerApplications(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var order = new InnerApplicationOrder(roots, refs);
        void Rewrite(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            // Frozen string-carried facts and the declaration semantic
            // signature are not structured own-type slots in this model.
            node.RewriteOwnTypeFieldsInPlace((_, _, type) => ProjectInnerApplication(type, order));
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.OverrideDeclaration) Rewrite(slot.Value);
            });
        }
        foreach (var root in roots) Rewrite(root);
    }

    // Preserve the existing partial projection exactly. Fn traverses all its
    // source signature slots, but FnPtr/Mod/Ptr are whole untouched containers.
    // In particular this must not become the full capture mapper above.
    static TypeNode ProjectInnerApplication(TypeNode type, InnerApplicationOrder order) => type switch
    {
        TypeNode.Fqn named => ProjectInnerClassifier(named, order),
        TypeNode.Nullable nullable => new TypeNode.Nullable(ProjectInnerApplication(nullable.Of, order)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ProjectInnerApplication(oblivious.Of, order)),
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, ProjectInnerApplication(projection.Of, order)),
        TypeNode.Array array => new TypeNode.Array(ProjectInnerApplication(array.Elem, order)),
        TypeNode.ByRef byRef => new TypeNode.ByRef(ProjectInnerApplication(byRef.Of, order)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend, ProjectInnerApplication(function.Ret, order),
            function.Params.Select(type => ProjectInnerApplication(type, order)).ToArray(),
            function.Recv == null ? null : ProjectInnerApplication(function.Recv, order), function.Clr,
            function.Ctx?.Select(type => ProjectInnerApplication(type, order)).ToArray()),
        _ => type,
    };

    static TypeNode.Fqn ProjectInnerClassifier(TypeNode.Fqn type, InnerApplicationOrder order)
    {
        if (type.Args == null) return type;
        var arguments = type.Args.Select(argument => ProjectInnerApplication(argument, order)).ToArray();
        var ordered = order.DeclarationArguments(type with { Args = arguments });
        return ReferenceEquals(arguments, ordered) ? type with { Args = arguments } : new TypeNode.Fqn(type.Name, ordered);
    }
}
