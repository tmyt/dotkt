using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ErasedElementValues
{
    // These entries repair already-selected capture storage after realization.
    // Do not replay scalar/array/iterator recognition, or copy continuing bodies.
    internal static void ApplyType(IBirTypeDeclaration type, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, IBirTypeDeclaration> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        bool IsInterface(TypeNode value) => value is TypeNode.Fqn named
            && (definitions.TryGetValue(named.Name, out var declaration) ? declaration.Kind.OrDefault() == "interface"
                : references.IsInterfaceType(named));
        foreach (var method in type.Methods.OrEmpty())
            ProjectSource(method, type, IsInterface, frames, isValue, policy, null, preserveOwnership: true);
        foreach (var constructor in type.Ctors.OrEmpty())
            ProjectSource(constructor, type, IsInterface, frames, isValue, policy, null, preserveOwnership: true);
    }

    internal static void ApplySuspendBody(BirNewSuspendLambda lambda, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, IBirTypeDeclaration> definitions,
        IReadOnlyDictionary<string, NullableRepresentationFrame> frames, ValueTypeOracle isValue,
        GenericRepresentationPolicy policy)
    {
        bool IsInterface(TypeNode value) => value is TypeNode.Fqn named
            && (definitions.TryGetValue(named.Name, out var declaration) ? declaration.Kind.OrDefault() == "interface"
                : references.IsInterfaceType(named));
        ProjectSource(lambda, null, IsInterface, frames, isValue, policy, null, preserveOwnership: true);
    }
}
