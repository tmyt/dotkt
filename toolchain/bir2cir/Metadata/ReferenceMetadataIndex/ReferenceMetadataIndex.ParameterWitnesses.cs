using System.Text.Json.Nodes;
using DotKt.Bir;

sealed partial class ReferenceMetadataIndex
{
    bool IsExistentialValueParameter(TypeNode type) => type switch {
        TypeNode.Nullable nullable => IsExistentialValueParameter(nullable.Of),
        TypeNode.Oblivious oblivious => IsExistentialValueParameter(oblivious.Of),
        TypeNode.Fqn named => IsExistentialPhysicalOwner(named.Name),
        _ => false,
    };

    // Value slots may be nominal carriers while a captured method still needs its source constructor's frame.
    // Consume the producer's KotlinType correspondence, not the carrier name or a method body.
    internal TypeNode[] CapturedParameterWitnesses(string identity, IReadOnlyList<TypeNode> physical)
    {
        if (identity == null || !_declarationById.TryGetValue(identity, out var binding)) return physical.ToArray();
        var frames = new InnerApplicationFrames(Array.Empty<JsonNode>(), this)
            .Project(_ownerNullableFrames, retainPhysicalOrder: true);
        foreach (var (semantic, named) in _physicalTypeBySemanticName)
            if (frames.TryGetValue(named, out var frame)) frames[semantic] = frame;
        var policy = new GenericRepresentationPolicy(_ownerAlias, this);
        var types = new NullableRepresentationTypes(SourceHierarchyFrame(binding.Owner), binding.NullableFrame,
            frames, IsValueType, policy: policy, nativeOwnerSlots: policy.UsesNativeOwnerSlots(binding.Owner));
        TypeNode Core(TypeNode type) => type switch {
            TypeNode.Nullable nullable => Core(nullable.Of),
            TypeNode.Oblivious oblivious => Core(oblivious.Of),
            _ => type,
        };
        return physical.Select((type, index) =>
        {
            var source = binding.KotlinParameterTypes?.ElementAtOrDefault(index);
            if (Core(type) is not TypeNode.Fqn actual || Core(source) is not TypeNode.Fqn logical) return type;
            var carrierValue = TryExistentialPhysicalOwner(logical.Name, out var carrier) && actual.Name == carrier;
            // Opaque transport does not erase the producer's declared alias construction.
            var opaqueAliasValue = actual.Name is "object" or "System.Object"
                && AliasVarianceRepresentation.RequiresErasure(logical, this);
            if (!carrierValue && !opaqueAliasValue) return type;
            return BirTypeLowering.LowerPhysicalType(types.Slot(source), Aliases, IsValueType, PhysicalTypeNames,
                typeArg: false, nullableFrames: opaqueAliasValue ? frames : null);
        }).ToArray();
    }
}
