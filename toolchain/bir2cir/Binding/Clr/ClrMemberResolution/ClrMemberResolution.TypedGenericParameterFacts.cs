using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // Echo<T>(T) can carry the opaque projected object unchanged. A slot such
    // as List<T> instead commits the invocation to the exact material T.
    internal static bool HasInvariantGenericUse(BirGraphValue call, int index, ReferenceMetadataIndex refs)
    {
        var selected = call switch {
            BirGenericStatic s when s.Type.OrDefault() is TypeNode.Fqn owner && s.ResolvedMemberParams.HasValue =>
                SelectedGenericProofDeclaration(null, owner, s.Method.Value, s.TypeArgs.Value.Length, false,
                    s.ResolvedMemberParams.Value, refs, nativeMethodKey: s.NativeMethodKey.OrDefault()),
            BirGenericInstance s when s.Type.OrDefault() is TypeNode.Fqn owner && s.ResolvedMemberParams.HasValue =>
                SelectedGenericProofDeclaration(null, owner, s.Method.Value, s.TypeArgs.Value.Length, true,
                    s.ResolvedMemberParams.Value, refs, nativeMethodKey: s.NativeMethodKey.OrDefault()),
            _ => null,
        };
        if (selected == null) return false;
        var variable = selected.GetGenericArguments()[index];
        bool Uses(Type type)
        {
            if (type.HasElementType) return Uses(type.GetElementType());
            if (!type.IsGenericType) return false;
            var arguments = type.GetGenericArguments();
            var parameters = type.GetGenericTypeDefinition().GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
                if ((arguments[i] == variable && (parameters[i].GenericParameterAttributes &
                        System.Reflection.GenericParameterAttributes.VarianceMask) == 0) || Uses(arguments[i])) return true;
            return false;
        }
        return Uses(selected.ReturnType) || selected.GetParameters().Any(p => Uses(p.ParameterType));
    }

    // These are the selected native MethodDef's physical CLR parameter facts,
    // not a Kotlin signature or a source-frame re-projection. The existing
    // metadata builder preserves declaration-relative owner/method variables,
    // all CLR special flags and absent unconstrained/variance fields.
    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(
        IReadOnlySet<string> bindingLocalTypes, TypeNode.Fqn owner, string name, int arity, bool instance, IReadOnlyList<TypeNode> parameters,
        ReferenceMetadataIndex refs, TypeNode[] parameterOwnerArguments = null, string nativeMethodKey = null)
        => SelectedGenericProofDeclaration(bindingLocalTypes, owner, name, arity, instance, parameters, refs, parameterOwnerArguments, nativeMethodKey)
            .GetGenericArguments().Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray();

    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(IReadOnlySet<string> bindingLocalTypes, BirGenericStatic call, ReferenceMetadataIndex refs)
        => SelectedMethodTypeParametersInPhysicalFrame(bindingLocalTypes, call.Type.OrDefault(), call.Method.OrDefault(),
            call.TypeArgs.OrEmpty().Length, false, call.ResolvedMemberParams.OrDefault(), refs, call.NativeMethodKey.OrDefault());

    internal static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(IReadOnlySet<string> bindingLocalTypes, BirGenericInstance call, ReferenceMetadataIndex refs)
        => SelectedMethodTypeParametersInPhysicalFrame(bindingLocalTypes, call.Type.OrDefault(), call.Method.OrDefault(),
            call.TypeArgs.OrEmpty().Length, true, call.ResolvedMemberParams.OrDefault(), refs, call.NativeMethodKey.OrDefault());

    // Presence is an authored selection fact: [] selects a zero-parameter
    // MethodDef; absent/null never falls back to a unique name or generic arity.
    static BirTypeParameter[] SelectedMethodTypeParametersInPhysicalFrame(
        IReadOnlySet<string> bindingLocalTypes, TypeNode owner, string name, int arity, bool instance, IReadOnlyList<TypeNode> parameters, ReferenceMetadataIndex refs, string nativeMethodKey)
    {
        if (owner is not TypeNode.Fqn selected || parameters == null || parameters.Any(type => type == null))
            throw new InvalidOperationException("Native generic proof demand lacks its selected declaration descriptor");
        return SelectedMethodTypeParametersInPhysicalFrame(bindingLocalTypes, selected, name, arity, instance, parameters, refs, nativeMethodKey: nativeMethodKey);
    }
}
