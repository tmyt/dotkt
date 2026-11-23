using static ReferenceMetadataReader;
using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class ReferenceMetadataIndex
{
    internal bool IsInvariantForeignConstruction(TypeNode.Fqn type) =>
        ResolveForeignProjectionType(type.Name, type.Args) is { IsGenericTypeDefinition: true } definition
        && definition.GetGenericArguments().All(p =>
            (p.GenericParameterAttributes & System.Reflection.GenericParameterAttributes.VarianceMask) == 0)
        && type.Args?.Any(arg => arg is TypeNode.Projection or TypeNode.Star) != true;

    readonly Dictionary<(string Id, bool NativeNames), ProjectedConstructorPlan> constructorPlans = new();
    internal ProjectedMethodPlan ProjectedMethod(string id)
    {
        if (SelectedReferenceMethod(id) is not { } declaration) return null;
        var parameters = PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true)
            ?? throw new InvalidOperationException($"No shipped projected method '{id}'");
        return ReadProjectedMethodPlan(declaration, parameters, WitnessConstraintKey);
    }
    internal TypeNode WitnessConstraintKey(TypeNode type) =>
        ClrMemberResolution.MethodImplComparisonType(PhysicalConstraintType(type), this);
    internal ProjectedConstructorPlan ConstructorPlan(string id, bool nativeNames)
    {
        if (constructorPlans.TryGetValue((id, nativeNames), out var plan)) return plan;
        if (SelectedReferenceMethod(id) is not { } declaration) return null;
        var shipped = ShippedMethod(declaration) ?? throw new InvalidOperationException($"No shipped constructor factory '{id}'");
        return constructorPlans[(id, nativeNames)] = ReadConstructorPlan(declaration, shipped, WitnessConstraintKey, nativeNames);
    }

    // Unlike OwnerTypeParametersInPhysicalFrame, these rows are reflected from
    // the actual shipped CLR TypeDef, including a required runtime twin. Source
    // metadata cannot supply a physical proof absent from that declaration.
    // Return independent records rather than exposing cached mutable facts.
    internal BirTypeParameter[] PhysicalOwnerTypeParametersInPhysicalFrame(string owner, bool nativeNames = false)
        => PhysicalOwnerForParameterFacts(owner) is { } type
            ? type.GetGenericArguments().Select(p => GenericParamDeclaration(p, nativeNames)).ToArray() : null;

    // The identity selects an exact reference MethodDef token, then the existing
    // twin resolver selects its shipped declaration. Do not reuse the owner's
    // local/admission guard or a source parameter row as another selector here.
    internal BirTypeParameter[] PhysicalMethodTypeParametersInPhysicalFrame(string identity, bool nativeNames = false)
        => SelectedPhysicalMethod(identity) is { } method
            ? method.GetGenericArguments().Select(p => GenericParamDeclaration(p, native: nativeNames)).ToArray() : null;
}
