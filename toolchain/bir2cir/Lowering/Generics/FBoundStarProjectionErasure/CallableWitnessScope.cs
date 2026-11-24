using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A lexical !i and !!i never refer to the same binder. Keep the producer's
// ordering: each appended formal is evidence for a particular ordinary slot.
readonly record struct CallableWitnessScope(BirTypeParameter[] Parameters, ProjectedClassStorageFrame.WitnessSlot[] Slots,
    NullableRepresentationFrame Representations = null)
{
    internal static TypeNode.Tv SplitNullable(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv v) return null;
        var scope = v.Scope == "method" ? method : owner;
        if (scope.Representations is not { } map || v.I >= map.PhysicalArity ||
            map.SourceIndex(v.I) is not int source || !map.NullableIndices.Contains(source)) return null;
        var nullable = map.NullableVariable(new TypeNode.Tv(v.Scope, source));
        return scope.Parameters[v.I].Constraints.OrEmpty().Contains(nullable) ? null : nullable;
    }

    internal static IEnumerable<(ProjectedClassStorageFrame.WitnessSlot Slot, TypeNode.Tv Variable)> For(
        TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv v) yield break;
        var scope = v.Scope == "method" ? method : owner;
        for (int i = 0; i < (scope.Slots?.Length ?? 0); i++)
            if (scope.Slots[i].OwnerIndex == v.I)
                yield return (scope.Slots[i], new TypeNode.Tv(v.Scope, scope.Parameters.Length + i));
    }
}
