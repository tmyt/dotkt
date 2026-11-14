using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierSurfacePreparation(CarrierDemandPlan Demand, SourceMethodSignatures Signatures);

    internal static CarrierSurfacePreparation PrepareCarrierSurface(CurrentDeclarationPreparation declarations,
        NativeProofPlan nativeProofs, ReferenceMetadataIndex references, GenericRepresentationPolicy values)
    {
        var demand = PrepareCarrierDemand(declarations, nativeProofs, references, values);
        // Bound registration observes the original methods. Accessors are
        // appended afterwards and enter only the later signature snapshot.
        PrepareMethodBounds(declarations.Bindings, references);
        foreach (var owner in demand.Owners.Values.Where(owner => owner.Needed))
            AddFieldAccessorsSource(owner.Name, owner.Arity,
                (IBirTypeDeclaration)owner.DefinitionBinding.Source, declarations.Bindings);
        return new(demand, CaptureSourceSignatures(declarations.Bindings));
    }
}
