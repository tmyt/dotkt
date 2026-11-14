using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    // These are the selected native MethodDef's physical CLR parameter facts,
    // not a Kotlin signature or a source-frame re-projection. The existing
    // metadata builder preserves declaration-relative owner/method variables,
    // all CLR special flags and absent unconstrained/variance fields.
    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(
        TypeNode.Fqn owner, string name, int arity, bool instance, IReadOnlyList<TypeNode> parameters,
        ReferenceMetadataIndex refs, TypeNode[] parameterOwnerArguments = null)
        => SelectedGenericProofDeclaration(owner, name, arity, instance, parameters, refs, parameterOwnerArguments)
            .GetGenericArguments().Select(ReferenceMetadataIndex.GenericParamSourceDeclaration).ToArray();

    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(BirGenericStatic call, ReferenceMetadataIndex refs)
        => SelectedMethodTypeParametersInPhysicalFrame(call.Type.OrDefault(), call.Method.OrDefault(),
            call.TypeArgs.OrEmpty().Length, false, call.ResolvedMemberParams.OrDefault(), refs);

    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(BirGenericInstance call, ReferenceMetadataIndex refs)
        => SelectedMethodTypeParametersInPhysicalFrame(call.Type.OrDefault(), call.Method.OrDefault(),
            call.TypeArgs.OrEmpty().Length, true, call.ResolvedMemberParams.OrDefault(), refs);

    // Presence is an authored selection fact: [] selects a zero-parameter
    // MethodDef; absent/null never falls back to a unique name or generic arity.
    static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(
        TypeNode owner, string name, int arity, bool instance, IReadOnlyList<TypeNode> parameters, ReferenceMetadataIndex refs)
    {
        if (owner is not TypeNode.Fqn selected || parameters == null || parameters.Any(type => type == null))
            throw new InvalidOperationException("Native generic proof demand lacks its selected declaration descriptor");
        return SelectedMethodTypeParametersInPhysicalFrame(selected, name, arity, instance, parameters, refs);
    }
}
