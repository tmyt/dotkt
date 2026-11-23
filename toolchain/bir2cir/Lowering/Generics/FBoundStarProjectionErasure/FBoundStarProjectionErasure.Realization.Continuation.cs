using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierRealizationContinuation(IReadOnlyDictionary<string, string> LocalOwners,
        IReadOnlyList<ProjectedStorageOwner> ProjectedStorage, IReadOnlySet<string> ProjectedInnerFactories,
        Dictionary<string, HashSet<(int Primary, TypeNode Bound)>> CallableFacets);

    // Later physical owners need the already chosen carrier names, not ended
    // source declarations or another mutable projection of their signatures.
    internal static CarrierRealizationContinuation CompleteRealization(CarrierRealizationContext context)
    {
        var facets = context.NativeProofs.CompleteCallableFacets(context.Declarations);
        context.Demand.CompleteSourceLifetime();
        context.Surface.Signatures.CompleteSourceLifetime();
        OwnerConstrainedMethodLowering.CompleteMethodPreparation(context.Bindings, context.References);
        var result = new CarrierRealizationContinuation(context.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal), context.ProjectedStorage.ToArray(),
            context.Declarations.ProjectedInnerFactories.Keys.ToHashSet(StringComparer.Ordinal), facets);
        context.Declarations.ReleaseSourceRows();
        return result;
    }
}
