using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinCollectionSlotSynthesis
{
    internal static void EraseGenericQueryParameters(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> aliases)
    {
        var definitions = new Dictionary<string, KotlinGenericScalarSlots.SourceOwner>(StringComparer.Ordinal);
        foreach (var owner in KotlinGenericScalarSlots.SourceOwners(roots))
            if (owner.IsType && owner.Name != null) definitions[owner.Name] = owner;
        bool IsInterface(TypeNode type) => type is TypeNode.Fqn named
            && (definitions.TryGetValue(named.Name, out var owner) ? owner.Kind == "interface" : refs?.IsInterfaceType(named) == true);
        foreach (var owner in definitions.Values.Where(owner => !aliases.ContainsKey(owner.Name)))
        {
            var ownerBounds = new ErasedElementValues.BoundDeclaration(owner.Facts?.NullableFrame,
                owner.TypeParameters.Select(parameter => parameter.Constraints.OrEmpty()).ToArray(), owner.Facts?.Bounds);
            foreach (var method in owner.Methods)
            {
                var marker = ErasedQueryOverride(method.Overrides.OrEmpty());
                if (marker == null || !method.Params.HasValue) continue;
                var slot = Slots.First(slot => slot.Parameters.Contains(ParameterCarrier.ErasedValue)
                    && MatchesOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
                        marker.Arity.HasValue ? marker.Arity.Value : null, slot));
                var methodBounds = new ErasedElementValues.BoundDeclaration(method.NullableRepresentationFrame.OrDefault(),
                    method.TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray(),
                    method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds);
                for (var index = 0; index < slot.Parameters.Length; index++)
                    if (slot.Parameters[index] == ParameterCarrier.ErasedValue
                        && GenericQueryBound(method.Params.Value[index].Type.OrDefault(), methodBounds, ownerBounds, IsInterface) is { } bound)
                    {
                        var parameter = method.Params.Value[index];
                        if (!parameter.KotlinType.HasValue) parameter.KotlinType = parameter.Type;
                        parameter.Type = bound;
                    }
            }
        }
    }

    internal static bool IsErasedQueryParameter(IEnumerable<BirOverride> overrides, int index) =>
        ErasedQueryOverride(overrides) is { } marker && Slots.Any(slot =>
            MatchesOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
                marker.Arity.HasValue ? marker.Arity.Value : null, slot)
            && index < slot.Parameters.Length && slot.Parameters[index] == ParameterCarrier.ErasedValue);
}
