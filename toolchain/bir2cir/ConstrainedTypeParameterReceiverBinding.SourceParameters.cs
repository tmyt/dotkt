using DotKt.Bir;
using DotKt.Cir;
using DotKt.Bir.Lowering;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static BirTypeParameter[] CloneMethodParametersWithErasedSourceBounds(BirFunctionDeclaration method) =>
        CloneSourceParameters(method.TypeParams.OrEmpty(), method._ownerConstraintDispatchBounds.OrDefault(),
            method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds, null);

    internal static BirTypeParameter[] CloneTypeParametersWithErasedSourceBounds(IBirTypeDeclaration type)
    {
        var facts = NullableRepresentationViews.TypeFacts((BirGraphValue)type);
        return CloneSourceParameters(TypeParameterFrame.Declarations(type).ToArray(),
            DispatchBounds((BirGraphValue)type), facts?.Bounds, facts?.NullableFrame);
    }

    // Restore the same declaration-owned erased dispatch edges as the late DOM
    // reader. Clone only header facts, never the owning executable declaration.
    static BirTypeParameter[] CloneSourceParameters(BirTypeParameter[] parameters,
        OwnerConstraintDispatchBounds dispatch, IReadOnlyDictionary<int, TypeNode[]> source,
        NullableRepresentationFrame frame)
    {
        var result = parameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        void Set(int index, TypeNode[] bounds)
        {
            if (index >= 0 && index < result.Length)
                result[index].ErasedInnerConstraints = bounds.Select(BirGraphValue.CopyType).ToArray();
        }
        if (dispatch != null)
            foreach (var (index, bounds) in dispatch.Bounds) Set(index, bounds);
        if (source != null)
            foreach (var (index, bounds) in ProjectedSourceBounds(source, result.Length, 0, frame)) Set(index, bounds);
        return result;
    }
}
