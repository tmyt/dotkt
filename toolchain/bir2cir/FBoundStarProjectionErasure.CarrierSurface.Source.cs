using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    internal sealed record CarrierSurfacePreparation(CarrierDemandPlan Demand, SourceMethodSignatures Signatures);

    internal static CarrierSurfacePreparation PrepareCarrierSurface(CurrentDeclarationPreparation declarations,
        NativeProofPlan nativeProofs, ReferenceMetadataIndex references, GenericRepresentationPolicy values,
        Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> materializedMethods = null)
    {
        var demand = PrepareCarrierDemand(declarations, nativeProofs, references, values);
        // Bound registration observes the original methods. Accessors are
        // appended afterwards and enter only the later signature snapshot.
        PrepareMethodBounds(declarations.Bindings, references);
        foreach (var owner in demand.Owners.Values.Where(owner => owner.Needed))
        {
            var definition = (IBirTypeDeclaration)owner.DefinitionBinding.Source;
            var appended = AddFieldAccessorsSource(owner.Name, owner.Arity, definition, declarations.Bindings);
            if (appended.Count > 0) materializedMethods?.Invoke(definition, appended);
        }
        return new(demand, CaptureSourceSignatures(declarations.Bindings));
    }
}
