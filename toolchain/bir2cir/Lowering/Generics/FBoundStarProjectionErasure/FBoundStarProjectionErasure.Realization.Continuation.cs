using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierRealizationContinuation(IReadOnlyDictionary<string, string> LocalOwners);

    // Later physical owners need the already chosen carrier names, not ended
    // source declarations or another mutable projection of their signatures.
    internal static CarrierRealizationContinuation CompleteRealization(CarrierRealizationContext context)
    {
        context.Demand.CompleteSourceLifetime();
        context.Surface.Signatures.CompleteSourceLifetime();
        OwnerConstrainedMethodLowering.CompleteMethodPreparation(context.Bindings, context.References);
        var result = new CarrierRealizationContinuation(context.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal));
        context.Declarations.ReleaseSourceRows();
        return result;
    }
}
