using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierRealizationContinuation(IReadOnlyDictionary<string, string> LocalOwners,
        Dictionary<string, string> PhysicalDeclarationSources);

    // The frontier changes addresses, not membership or representation policy.
    // Later passes need the chosen names and declaration identities, not a
    // second catalog of carrier synthesis proposals or source executable trees.
    internal static CarrierRealizationContinuation BindRealizationContinuation(CarrierRealizationContext context)
    {
        var preparation = context.Preparation;
        var definitions = preparation.BindDefinitions();
        _ = context.Demand.BindOwners();
        _nativeProofs = context.NativeProofs;
        _valueSlots = context.Values;
        _usedCarrierNames = context.ReservedNames;
        _valueSlots.RegisterLocalDefinitions(definitions, preparation.LocalClrAliases);
        _sourceSupertypes = preparation.BindSourceSupertypes();
        _localMethods = preparation.BindLocalMethods();
        _localDeclarations = preparation.BindLocalDeclarations();
        var owners = context.Owners;
        var references = context.References;
        var aliases = preparation.LocalClrAliases;
        ForeignStarProjectionBinding.ContinueWithProjection((type, shape) =>
            RewriteType(type, owners, references, localClrAliases: aliases, preserveArrayShape: shape));
        _ = context.Surface.Signatures.BindPhysical();
        OwnerConstrainedMethodLowering.ConfigurePreparedMethods(preparation.Bindings, context.References,
            bound => PhysicalBoundClassifier(bound, context.Owners, context.References),
            (method, index) => _nativeProofs.Bounds(method, index));
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var owner in context.Owners.Values.Where(owner => owner.Needed))
        foreach (var row in context.Declarations.Slots(owner).Values)
            if (Str(row.Original.Physical[DeclarationIdentityBinding.Key]) is string original
                && row.Slot.DeclarationId.OrDefault() is string physical)
                sources[physical] = original;
        var result = new CarrierRealizationContinuation(context.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal), sources);
        context.Declarations.ReleaseSourceRows();
        return result;
    }
}
