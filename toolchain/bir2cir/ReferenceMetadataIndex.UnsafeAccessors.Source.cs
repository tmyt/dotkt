using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class ReferenceMetadataIndex
{
    internal sealed record SourceUnsafeAccessorMethod(string PhysicalMember, TypeNode[] Parameters, TypeNode Return,
        BirTypeParameter[] TypeParams, TypeNode NullableGenericReturn, bool IsPublic);

    // These are the selected metadata row's canonical declaration parameters,
    // not a projection of a compiler graph or a replacement reflection search.
    internal bool TryUnsafeAccessorSourceMethod(string id, string owner, int arity, bool isStatic,
        out SourceUnsafeAccessorMethod declaration)
    {
        declaration = null;
        if (!TryUnsafeAccessorMethod(id, owner, arity, isStatic, out var selected)) return false;
        declaration = new(selected.PhysicalMember, selected.Parameters, selected.Return,
            selected.TypeParams, selected.NullableGenericReturn, selected.IsPublic);
        return true;
    }

    internal bool TryUnsafeAccessorVirtualSourceMethod(string owner, string name, int arity, bool isStatic,
        IReadOnlyList<TypeNode> signature, TypeNode result, TypeNode[] ownerArguments,
        BirTypeParameter[] selectedParameters, out SourceUnsafeAccessorMethod declaration)
    {
        declaration = null;
        var selected = SelectUnsafeAccessorVirtualMethod(owner, name, arity, isStatic, signature, result,
            ownerArguments, KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(selectedParameters, ownerArguments));
        if (selected == null) return false;
        declaration = new(selected.Name, selected.SignatureParameters, selected.SignatureReturn,
            CloneReferencedTypeParameters(selected.MethodTypeParams), selected.NullableGenericRet, selected.IsPublic);
        return true;
    }
}
