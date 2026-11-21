using static ReferenceMetadataReader;
using DotKt.Bir.Lowering;

sealed partial class ReferenceMetadataIndex
{
    // Unlike OwnerTypeParametersInPhysicalFrame, these rows are reflected from
    // the actual shipped CLR TypeDef, including a required runtime twin. Source
    // metadata cannot supply a physical proof absent from that declaration.
    // Return independent records rather than exposing cached mutable facts.
    internal BirTypeParameter[] PhysicalOwnerTypeParametersInPhysicalFrame(string owner)
        => PhysicalOwnerForParameterFacts(owner) is { } type
            ? type.GetGenericArguments().Select(GenericParamSourceDeclaration).ToArray() : null;

    // The identity selects an exact reference MethodDef token, then the existing
    // twin resolver selects its shipped declaration. Do not reuse the owner's
    // local/admission guard or a source parameter row as another selector here.
    internal BirTypeParameter[] PhysicalMethodTypeParametersInPhysicalFrame(string identity, bool nativeNames = false)
        => SelectedPhysicalMethod(identity) is { } method
            ? method.GetGenericArguments().Select(p => GenericParamDeclaration(p, native: nativeNames)).ToArray() : null;
}
