using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    // The selected declaration supplies the slots; these writers neither search
    // again nor change the caller's owning argument occurrences.
    internal static void StampNativeDelegateArgumentTargets(IBirExpression[] arguments,
        TypeNode[] methodArguments, MethodBase target, TypeNode[] ownerArguments)
    {
        if (arguments == null || !CanCloseNativeDelegateArguments(target, methodArguments)) return;
        var parameters = target.GetParameters();
        if (arguments.Length != parameters.Length) return;
        for (var index = 0; index < arguments.Length; index++)
            if (arguments[index] is BirGraphValue argument)
                MarkDelegateSlot(argument, CloseDelegateArgumentSlot(RefTypeOf(parameters[index].ParameterType),
                    ownerArguments, methodArguments), _refs, _localTypes);
    }

    static bool CanCloseNativeDelegateArguments(MethodBase target, TypeNode[] arguments)
        // A leftover callee variable must never be read as a caller variable.
        => arguments != null && !arguments.Any(type => type == null)
            && (target is not MethodInfo { IsGenericMethodDefinition: true } method
                || arguments.Length == method.GetGenericArguments().Length);

    internal static OptionalField<BirTypeParameter[]> NativeResolvedMethodTypeParameters(
        MethodInfo target, int argumentCount)
    {
        var parameters = ResolvedMethodTypeParameters(target, argumentCount);
        return parameters == null ? default : OptionalField<BirTypeParameter[]>.Of(
            parameters.Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray());
    }
}
