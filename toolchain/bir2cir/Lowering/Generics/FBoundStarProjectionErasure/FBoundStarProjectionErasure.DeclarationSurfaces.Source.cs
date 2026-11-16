using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static bool ContainsGenericAlias(TypeNode type, GenericRepresentationPolicy values) => type switch {
        TypeNode.Fqn { Args: { } arguments } named => values?.IsBindingAlias(named.Name) == true
            || arguments.Any(argument => ContainsGenericAlias(argument, values)),
        TypeNode.Nullable value => ContainsGenericAlias(value.Of, values),
        TypeNode.Oblivious value => ContainsGenericAlias(value.Of, values),
        TypeNode.Projection value => ContainsGenericAlias(value.Of, values),
        TypeNode.Array value => ContainsGenericAlias(value.Elem, values),
        TypeNode.ByRef value => ContainsGenericAlias(value.Of, values),
        TypeNode.Ptr value => ContainsGenericAlias(value.Of, values),
        TypeNode.Mod value => ContainsGenericAlias(value.M, values) || ContainsGenericAlias(value.Of, values),
        TypeNode.Fn value => ContainsGenericAlias(value.Ret, values) || value.Params.Any(parameter => ContainsGenericAlias(parameter, values))
            || value.Recv != null && ContainsGenericAlias(value.Recv, values)
            || value.Ctx?.Any(parameter => ContainsGenericAlias(parameter, values)) == true,
        _ => false,
    };

    static bool ContainsKotlinVariantType(TypeNode type, CarrierProjectionContext context) => type switch {
        TypeNode.Fqn named => context.RequiresKotlinValueCarrier(named) || context.RequiresOpaqueVariantAlias(named)
            || named.Args?.Any(argument => ContainsKotlinVariantType(argument, context)) == true,
        TypeNode.Nullable value => ContainsKotlinVariantType(value.Of, context),
        TypeNode.Oblivious value => ContainsKotlinVariantType(value.Of, context),
        TypeNode.Projection value => ContainsKotlinVariantType(value.Of, context),
        TypeNode.Array value => ContainsKotlinVariantType(value.Elem, context),
        TypeNode.ByRef value => ContainsKotlinVariantType(value.Of, context),
        TypeNode.Ptr value => ContainsKotlinVariantType(value.Of, context),
        TypeNode.Mod value => ContainsKotlinVariantType(value.M, context) || ContainsKotlinVariantType(value.Of, context),
        TypeNode.Fn value => ContainsKotlinVariantType(value.Ret, context)
            || value.Params.Any(parameter => ContainsKotlinVariantType(parameter, context))
            || value.Recv != null && ContainsKotlinVariantType(value.Recv, context)
            || value.Ctx?.Any(parameter => ContainsKotlinVariantType(parameter, context)) == true,
        _ => false,
    };

    static bool ContainsWritableVariantArray(TypeNode type, CarrierProjectionContext context) => type switch {
        TypeNode.Array value => TryWritableVariantArrayElement(value.Elem, context, out _, out _)
            || ContainsWritableVariantArray(value.Elem, context),
        TypeNode.Fqn { Args: { } arguments } => arguments.Any(argument => ContainsWritableVariantArray(argument, context)),
        TypeNode.Nullable value => ContainsWritableVariantArray(value.Of, context),
        TypeNode.Oblivious value => ContainsWritableVariantArray(value.Of, context),
        TypeNode.Projection value => ContainsWritableVariantArray(value.Of, context),
        TypeNode.ByRef value => ContainsWritableVariantArray(value.Of, context),
        TypeNode.Ptr value => ContainsWritableVariantArray(value.Of, context),
        TypeNode.Mod value => ContainsWritableVariantArray(value.M, context) || ContainsWritableVariantArray(value.Of, context),
        TypeNode.Fn value => ContainsWritableVariantArray(value.Ret, context)
            || value.Params.Any(parameter => ContainsWritableVariantArray(parameter, context))
            || value.Recv != null && ContainsWritableVariantArray(value.Recv, context)
            || value.Ctx?.Any(parameter => ContainsWritableVariantArray(parameter, context)) == true,
        _ => false,
    };

    internal static void RecordDeclarationSurfacesSource(BirFileDeclaration root, CarrierRealizationContext context)
    {
        bool Moves(TypeNode type) => ContainsUseSiteProjection(type) || !context.Projection.Rewrite(type).Equals(type);
        Dictionary<int, TypeNode[]> Bounds(IReadOnlyList<BirTypeParameter> parameters, Func<TypeNode, bool> predicate)
            => parameters.Select((parameter, index) => (parameter, index))
                .Where(row => row.parameter.Constraints.HasValue && row.parameter.Constraints.Value.Any(predicate))
                .ToDictionary(row => row.index, row => row.parameter.Constraints.Value.Select(BirGraphValue.CopyType).ToArray());
        OptionalField<TypeNode> Slot(OptionalField<TypeNode> prior, TypeNode type, TypeNode nullable)
        {
            if (prior.HasValue || type == null || !ContainsExistentialProjection(type)
                && !ContainsWritableVariantArray(type, context.Projection) && !ContainsGenericArray(type)
                && !ContainsGenericAlias(type, context.Values) && !ContainsKotlinVariantType(type, context.Projection)) return prior;
            return BirGraphValue.CopyType(nullable ?? type);
        }
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration owner && owner.Kind.HasValue)
            {
                var facts = NullableRepresentationViews.TypeFacts(node) ?? SourceTypeFacts.Empty;
                if (facts.NullableFrame == null)
                    facts = facts.Merge(Bounds(owner.TypeParams.OrEmpty(), type => Moves(type) || ContainsStarOrTypeVariable(type)));
                var originalBase = owner.Base.OrDefault();
                facts = facts.Merge(originalBase != null && !context.Projection.Rewrite(originalBase, preserveConstructedHead: true).Equals(originalBase)
                        ? BirGraphValue.CopyType(originalBase) : null,
                    owner.Interfaces.OrEmpty().Where(edge => !context.Projection.Rewrite(edge, preserveConstructedHead: true).Equals(edge))
                        .Select(BirGraphValue.CopyType).ToArray(), null);
                if (!ReferenceEquals(facts, SourceTypeFacts.Empty)) NullableRepresentationViews.SetTypeFacts(node, facts);
            }
            if (node is BirFunctionDeclaration method && method.Name.HasValue && method.Params.HasValue)
            {
                if (!method.NullableRepresentationFrame.HasValue)
                {
                    var bounds = Bounds(method.TypeParams.OrEmpty(), Moves);
                    if (bounds.Count > 0)
                        method.MethodTypeParameterBoundsPre = (method.MethodTypeParameterBoundsPre.OrDefault()
                            ?? new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]>())).Merge(bounds);
                }
                method.RetKotlinType = Slot(method.RetKotlinType, method.Ret.OrDefault(), method.NullableGenericRet.OrDefault());
            }
            switch (node)
            {
                case BirParameter value: value.KotlinType = Slot(value.KotlinType, value.Type.OrDefault(), value.NullableGeneric.OrDefault()); break;
                case BirLambdaParameter value: value.KotlinType = Slot(value.KotlinType, value.Type.OrDefault(), value.NullableGeneric.OrDefault()); break;
                case BirCaptureDescriptor value: value.KotlinType = Slot(value.KotlinType, value.Type.OrDefault(), value.NullableGeneric.OrDefault()); break;
                case BirFieldDeclaration value: value.KotlinType = Slot(value.KotlinType, value.Type.OrDefault(), value.NullableGeneric.OrDefault()); break;
                case BirPropertyDeclaration value: value.KotlinType = Slot(value.KotlinType, value.Type.OrDefault(), value.NullableGeneric.OrDefault()); break;
            }
            node.VisitChildren(Walk);
        }
        Walk(root);
    }

    internal static void EraseProjectedAliasConstraintsSource(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((field, role, slot) => {
                // Only the actual typeParams field participates, not captured
                // frames, member-owner snapshots or referenced signature rows.
                if (field == "typeParams" && slot.Value is BirTypeParameter parameter && parameter.Constraints.HasValue)
                    for (var index = 0; index < parameter.Constraints.Value.Length; index++)
                        if (StripSourceNullability(parameter.Constraints.Value[index]) is TypeNode.Fqn bound
                            && ContainsUseSiteProjection(bound) && refs.TryResolveClrOwner(bound.Name, out _, out _))
                            parameter.Constraints.Value[index] = new TypeNode.Fqn("kotlin.Any");
                Walk(slot.Value);
            });
        }
        Walk(root);
    }
}
