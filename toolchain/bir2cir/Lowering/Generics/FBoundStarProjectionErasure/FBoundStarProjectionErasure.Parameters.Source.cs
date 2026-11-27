using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    // Parameter ABI is projected before bridges consume it. Preserve the actual
    // owning declaration/vector/record: the frontier binds those same objects.
    internal static void ProjectParameterDeclarations(IReadOnlyList<BirFileDeclaration> roots,
        CarrierProjectionContext context, IDictionary<string, int[]> physicalParameterIndices = null)
    {
        void Walk(BirGraphValue node, BirGraphValue parent = null, string parentField = null)
        {
            if (node is IBirExecutablePayload) return;
            var generatedConstructor = node is BirConstructor && parentField == "ctors"
                && parent is IBirTypeDeclaration { Generated: { HasValue: true, Value: true } };
            var method = node as BirFunctionDeclaration;
            var ordinary = method != null && parentField == "methods"
                && IsOrdinarySourceParameterMethod(method, parent, context);
            var parameterIndex = 0;
            node.VisitOwnChildSlots((field, _, slot) => {
                var child = slot.Value;
                if (field == "params" && child is BirParameter or BirLambdaParameter)
                {
                    var index = parameterIndex++;
                    if (!generatedConstructor)
                        ProjectSourceParameter(child, context, ordinary, method, index, physicalParameterIndices);
                }
                // Saved inline executable envelopes are opaque above. Ordinary
                // live default bodies and nested lambda parameters still walk.
                Walk(child, node, field);
            });
        }
        foreach (var root in roots) Walk(root);
    }

    static bool SourceParameterOwnerHasAlias(IEnumerable<BirAttribute> attributes) => attributes.Any(attribute =>
        attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" });

    static bool IsOrdinarySourceParameterMethod(BirFunctionDeclaration method, BirGraphValue owner,
        CarrierProjectionContext context)
    {
        if (!method.DeclarationId.HasValue || KotlinPropertyAccessors.TryIdentity(method, out _, out _)) return false;
        var (generated, alias) = owner switch {
            IBirTypeDeclaration type => (type.Generated.OrDefault(), SourceParameterOwnerHasAlias(type.Attrs.OrEmpty())),
            BirFileDeclaration file => (false, SourceParameterOwnerHasAlias(file.Attrs.OrEmpty())),
            _ => (false, false),
        };
        // Only an actual methods vector belongs to this policy; no lexical
        // owner name or generated-artifact spelling can establish membership.
        if (owner is not (IBirTypeDeclaration or BirFileDeclaration)) return false;
        return OrdinaryKotlinParameterPolicy(method.Generated.OrDefault(), generated, alias,
            method.Attrs.OrEmpty().Any(attribute =>
                attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrIntrinsic" }),
            method.Overrides.OrEmpty().Select(edge => (edge.Owner.OrDefault() as TypeNode.Fqn)?.Name),
            name => context.Preparation.Bindings.SourceDefinitions.TryGetValue(name, out var binding)
                ? SourceParameterOwnerHasAlias(((IBirTypeDeclaration)binding.Source).Attrs.OrEmpty()) : null,
            context.References);
    }

    static void ProjectSourceParameter(BirGraphValue parameter, CarrierProjectionContext context,
        bool ordinary, BirFunctionDeclaration method, int index, IDictionary<string, int[]> physicalParameterIndices)
    {
        var (type, outer, kotlinType, nullableGeneric) = parameter switch {
            BirParameter value => (value.Type, value.Outer.OrDefault(), value.KotlinType, value.NullableGeneric),
            BirLambdaParameter value => (value.Type, value.Outer.OrDefault(), value.KotlinType, value.NullableGeneric),
            _ => throw new InvalidOperationException("Not an owning parameter declaration"),
        };
        if (!type.HasValue) return;
        // Parameter records carry no operation/selected-descriptor role, so
        // IsBoundDeclarationType(parameter, "type") is always false. Their
        // explicit outer fact still owns the exact lexical constructed head.
        var projected = context.Rewrite(type.Value, preserveConstructedHead: outer);
        if (ordinary)
        {
            var valueSlot = context.ProjectOrdinaryParameter(projected);
            if (valueSlot != projected && physicalParameterIndices != null)
            {
                var identity = method.DeclarationId.Value;
                physicalParameterIndices[identity] =
                    (physicalParameterIndices.TryGetValue(identity, out var prior) ? prior : Array.Empty<int>())
                    .Append(index).Distinct().ToArray();
            }
            projected = valueSlot;
        }
        var projectedOverride = method?.DeclarationId.HasValue == true && !method.Generated.OrDefault()
            && method.Overrides.OrEmpty().Length != 0 && StripSourceNullability(type.Value) is TypeNode.Fqn named
            && ContainsExistentialProjection(named) && context.TryExistentialCarrier(named.Name, out var carrier)
            && StripSourceNullability(projected) is TypeNode.Fqn physical && physical.Name == carrier;
        if (!(ordinary || projectedOverride || context.IsCapturedInner(type.Value) || SignatureValueTypes.Of(type.Value) is TypeNode.ByRef)
            || projected == type.Value) return;

        // Frozen source facts must not share mutable nested argument vectors
        // with the live parameter slot.
        var source = kotlinType.HasValue ? kotlinType
            : OptionalField<TypeNode>.Of(BirGraphValue.CopyType(nullableGeneric.OrDefault() ?? type.Value));
        switch (parameter)
        {
            case BirParameter value: value.KotlinType = source; value.Type = projected; break;
            case BirLambdaParameter value: value.KotlinType = source; value.Type = projected; break;
        }
    }
}
