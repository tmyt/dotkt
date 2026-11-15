using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierRealizationContinuation(IReadOnlyDictionary<string, string> LocalOwners);

    // The frontier changes addresses, not membership or representation policy.
    // Later passes need the chosen names and declaration identities, not a
    // second catalog of carrier synthesis proposals or source executable trees.
    internal static CarrierRealizationContinuation BindRealizationContinuation(CarrierRealizationContext context,
        CurrentPhysicalTypeView currentPhysical = null)
    {
        var preparation = context.Preparation;
        var definitions = preparation.BindDefinitions();
        _ = context.Demand.BindOwners();
        _nativeProofs = context.NativeProofs;
        _valueSlots = context.Values;
        _usedCarrierNames = context.ReservedNames;
        _valueSlots.RegisterLocalDefinitions(definitions, preparation.LocalClrAliases);
        // Post-Cold/final-slot projection refreshes physical membership without
        // changing the original source catalogs. Carry those same producer-bound
        // records to genuine late readers, with current-first precedence.
        if (currentPhysical != null)
            _valueSlots.RegisterLocalDefinitions(currentPhysical.BindDefinitions(), currentPhysical.LocalClrAliases);
        _sourceSupertypes = preparation.BindSourceSupertypes();
        _localMethods = preparation.BindLocalMethods();
        _localDeclarations = preparation.BindLocalDeclarations();
        var owners = context.Owners;
        var references = context.References;
        var aliases = preparation.LocalClrAliases;
        ForeignStarProjectionBinding.ContinueWithProjection((type, shape) =>
            RewriteType(type, owners, references, localClrAliases: aliases, preserveArrayShape: shape));
        _ = context.Surface.Signatures.BindPhysical();
        OwnerConstrainedMethodLowering.ConfigurePreparedMethods(preparation.Bindings, context.References);
        var result = new CarrierRealizationContinuation(context.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal));
        context.Declarations.ReleaseSourceRows();
        return result;
    }
}
