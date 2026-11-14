using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class OwnerConstrainedMethodLowering
{
    // One registration changes address at the owning frontier. The physical
    // consumers never rebuild membership from names or declaration identities.
    sealed class MethodRegistration(DeclarationBinding method, DeclarationBinding owner)
    {
        internal DeclarationBinding MethodBinding => method;
        internal DeclarationBinding OwnerBinding => owner;
        internal JsonObject Method => method.Physical;
        internal JsonObject Owner => owner.Physical;
    }
    static CurrentDeclarationBindings MethodBindings;
    static bool PreparedMethods;

    internal static void BeginSourceMethodPreparation(CurrentDeclarationBindings bindings, ReferenceMetadataIndex references)
    {
        Methods.Clear();
        MethodBindings = bindings;
        PreparedMethods = true;
        References = references;
    }

    internal static void ConfigurePreparedMethods(CurrentDeclarationBindings bindings, ReferenceMetadataIndex references)
    {
        if (!PreparedMethods || !ReferenceEquals(bindings, MethodBindings) || !ReferenceEquals(references, References))
            throw new InvalidOperationException("Method preparation does not own this physical continuation");
        foreach (var row in Methods) { _ = row.Method; _ = row.Owner; }
        References = references;
        PreparedMethods = false;
    }

    internal static bool HasMethodDependentBounds(BirFunctionDeclaration method, ReferenceMetadataIndex references) =>
        HasMethodDependentBounds(ConstraintVectors(method.TypeParams.OrDefault()), method.NullableRepresentationFrame.OrDefault(), references);

    static bool HasMethodDependentBounds(IReadOnlyList<TypeNode[]> parameters, NullableRepresentationFrame frame,
        ReferenceMetadataIndex references) => parameters.Select((bounds, index) => bounds.Any(bound =>
            HasConstructedDependency(bound, "method", index, frame, references))).Any(found => found);

    static SourceBoundsSnapshot CaptureMethodBounds(SourceBoundsSnapshot prior, IReadOnlyDictionary<int, TypeNode[]> additions) =>
        (prior ?? new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]>())).Merge(additions.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(BirGraphValue.CopyType).ToArray()));

    internal static void Record(BirFunctionDeclaration method, BirGraphValue owner, CurrentDeclarationBindings bindings)
    {
        if (!PreparedMethods || !ReferenceEquals(bindings, MethodBindings))
            throw new InvalidOperationException("Source method registered outside its preparation");
        Methods.Add(new(bindings.GetSource(method), bindings.GetSource(owner)));
        // Materialized rows already own their Kotlin snapshot; companions are
        // physical dispatch facts, not another source bound capture.
        if (method.NullableRepresentationFrame.HasValue) return;
        var additions = method.TypeParams.OrEmpty().Select((parameter, index) => (parameter, index))
            .Where(row => row.parameter.Constraints.HasValue)
            .ToDictionary(row => row.index, row => row.parameter.Constraints.Value);
        var bounds = CaptureMethodBounds(method.MethodTypeParameterBoundsPre.OrDefault(), additions);
        if (bounds.Bounds.Count > 0) method.MethodTypeParameterBoundsPre = bounds;
    }
}
