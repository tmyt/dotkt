using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class MemberCallSubstitution
{
    // The selected Kotlin declaration and its current lexical operands remain
    // the owning occurrences. This stage alone chooses their CLR call shape;
    // no projected DOM graph is re-read to recover the source decision.
    internal static BirFileDeclaration Apply(BirFileDeclaration root, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localTopLevelFns, bool attributeTopLevelOwner, ValueTypeOracle isValue,
        IReadOnlyDictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>> localPropertyAccessors,
        GenericRepresentationPolicy representations, IReadOnlyDictionary<string, LocalFactory> localFactories,
        IReadOnlySet<string> localDeclarationIds = null)
    {
        _localTopLevelFns = localTopLevelFns;
        _attributeTopLevelOwner = attributeTopLevelOwner;
        _isValue = isValue ?? (_ => false);
        _representations = representations;
        _localFactories = localFactories;
        _localDeclarationIds = localDeclarationIds;
        _localPropertyAccessors = localPropertyAccessors
            ?? new Dictionary<LocalPropertyAccessorKey, IReadOnlyList<LocalPropertyAccessor>>();
        _localPropertyOwners = _localPropertyAccessors.Keys.Select(key => key.Owner).ToHashSet(StringComparer.Ordinal);
        _typesWithDeclaredIterator = CollectDeclaredIteratorTypes(root);
        _iteratorTypeDefinitions = SupertypeGraph.CollectSource(new[] { root });
        RewriteSource(root, refs, new CollectionHelperBinding.CollectionSourceContext());
        return root;
    }

    static BirGraphValue RewriteSource(BirGraphValue node, ReferenceMetadataIndex refs,
        CollectionHelperBinding.CollectionSourceContext context)
    {
        if (node is IBirExecutablePayload) return node;
        var childContext = context.Extend(node);
        node.VisitChildSlots(slot => slot.Replace(RewriteSource(slot.Value, refs, childContext)));
        // Declaration-wide capture already consumed value-spine provenance.
        // Only the chosen physical helper, not this transient marker, continues.
        if (node is BirCast cast) cast.DotktProjectedCollection = default;
        if (node is BirCallStatic call) call.DotktProjectedCollection = default;
        return node switch {
            BirNew value => TransformNew(value, refs) as BirGraphValue ?? value,
            BirSpreadConcat value => (BirGraphValue)TransformSpread(value, childContext),
            BirCallInstance value => TransformCall(value, refs, childContext) ?? value,
            BirCallStatic value => TransformCall(value, refs, childContext) ?? value,
            BirStaticFieldGet value => TransformStaticField(value, refs) as BirGraphValue ?? value,
            BirFieldGet value => TransformStorageField(value) as BirGraphValue ?? value,
            _ => node,
        };
    }
}
