using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A lexical !i and !!i never refer to the same binder. Keep the producer's
// ordering: each appended formal is evidence for a particular ordinary slot.
readonly record struct CallableWitnessScope(BirTypeParameter[] Parameters, ProjectedClassStorageFrame.WitnessSlot[] Slots,
    NullableRepresentationFrame Representations = null, IReadOnlyDictionary<int, TypeNode.Tv> CapturedNullable = null,
    ProjectedMethodPlan NativePlan = null, IReadOnlyDictionary<int, CallableWitnessScope.MaterialWitness> CapturedNative = null,
    IReadOnlyDictionary<int, TypeNode.Tv> CapturedStorage = null)
{
    internal sealed record MaterialWitness(TypeNode.Tv Variable, BirTypeParameter Formal);
    internal static TypeNode.Tv Storage(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv v) return null;
        var scope = v.Scope == "method" ? method : owner;
        if (scope.CapturedStorage?.TryGetValue(v.I, out var captured) == true) return captured;
        if (scope.Representations is not { } frame || v.I >= frame.PhysicalArity ||
            frame.SourceIndex(v.I) is not int source || frame.SourcePosition(source) != v.I || !frame.StorageIndices.Contains(source)) return null;
        return frame.Variable(new TypeNode.Tv(v.Scope, source), NullableRepresentationFrame.Role.Storage);
    }
    internal static MaterialWitness MaterialFact(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv v) return null;
        if (v.Scope == "type")
        {
            if (owner.CapturedNative?.GetValueOrDefault(v.I) is { } captured) return captured;
            foreach (var (slot, variable) in For(v, owner, method))
                if (slot.MaterialFormal is { } formal) return new(variable, formal);
            return null;
        }
        return method.NativePlan?.NativeArgument(v) is TypeNode.Tv n && n != v
            ? new(n, method.NativePlan.Parameters[n.I]) : null;
    }
    internal static TypeNode Material(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method) =>
        MaterialFact(argument, owner, method)?.Variable ?? argument;

    internal static TypeNode.Tv SplitNullable(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv v) return null;
        var scope = v.Scope == "method" ? method : owner;
        if (scope.CapturedNullable?.TryGetValue(v.I, out var captured) == true) return captured;
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

    // A reference-only source and its nullable view have the same CLR
    // value representation, not necessarily the same nominal class proof.
    internal static TypeNode.Tv ReferenceValue(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method,
        Func<TypeNode.Fqn,bool> isClass)
    {
        if (argument is not TypeNode.Tv v || SplitNullable(v, owner, method) is not { } nullable) return null;
        var row = (v.Scope == "method" ? method : owner).Parameters[v.I];
        return row.SpecialConstraints.OrEmpty().Contains("class") || row.Constraints.OrEmpty().OfType<TypeNode.Fqn>().Any(b =>
            !FBoundStarProjectionErasure.IsObjectish(b) && b.Name is not ("System.ValueType" or "System.Enum") && isClass(b))
            ? nullable : null;
    }

    // A source edge B : A stays intact when A's synthetic edge to N(A)
    // is removed. Its former transitive consequence B : N(A) does not.
    internal static TypeNode.Tv[] LostNullableBounds(TypeNode argument, CallableWitnessScope owner, CallableWitnessScope method)
    {
        if (argument is not TypeNode.Tv first || (method.Representations?.NullableIndices.Count ?? 0) == 0 &&
            (owner.Representations?.NullableIndices.Count ?? 0) == 0 && owner.CapturedNullable == null) return [];
        var reachable = new HashSet<TypeNode.Tv>();
        void Visit(TypeNode.Tv v)
        {
            if (!reachable.Add(v)) return;
            var scope = v.Scope == "method" ? method : owner;
            if (scope.Parameters == null || v.I >= scope.Parameters.Length) return;
            foreach (var upper in scope.Parameters[v.I].Constraints.OrEmpty().OfType<TypeNode.Tv>()) Visit(upper);
        }
        Visit(first);
        return reachable.Select(v => SplitNullable(v, owner, method)).Where(n => n != null && !reachable.Contains(n)).Distinct().ToArray();
    }
}
