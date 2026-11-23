using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The static factory's primary formals have the exact owner's physical order.
// Facets follow in entry order; their bounds are expressed in that owner binder.
sealed record ProjectedConstructorPlan(BirTypeParameter[] PrimaryParameters, BirTypeParameter[] WitnessParameters, int Captured, ProjectedConstructorPlan.Facet[] Facets)
{
    internal sealed record Facet(int Primary, TypeNode Bound, bool NativeArgument, TypeNode[] Constraints);
    internal int PrimaryArity => WitnessParameters.Length;

    // On an unsplit declaring type one CLR argument must satisfy the entire
    // interface conjunction. A class or dependent bound is a different contract.
    internal static IEnumerable<(int Primary, TypeNode[] Bounds)> InterfaceArguments(
        BirTypeParameter[] formals, Func<TypeNode.Fqn, bool> isInterface)
    {
        for (int i = 0; i < (formals?.Length ?? 0); i++)
        {
            var bounds = formals[i].Constraints.OrEmpty();
            if (formals[i].SpecialConstraints.OrEmpty().Length == 0 && bounds.Length > 0 &&
                bounds.All(b => b is TypeNode.Fqn f && isInterface(f))) yield return (i, bounds);
        }
    }

    internal static bool Proves(TypeNode available, TypeNode required,
        IReadOnlyDictionary<string, SupertypeGraph.Def> hierarchy, ReferenceMetadataIndex references)
    {
        if (available == required) return true;
        if (available is not TypeNode.Fqn root || required is not TypeNode.Fqn target) return false;
        TypeNode Identity(TypeNode.Fqn f) => hierarchy.ContainsKey(f.Name) ? f : references.WitnessConstraintKey(f);
        var key = Identity(target);
        var expected = !hierarchy.ContainsKey(target.Name) && key is TypeNode.Fqn to
            ? FBoundStarProjectionErasure.ResolveClosedReferenceType(to, references, physicalTypes: true) : null;
        return SupertypeGraph.ReachableFromClass(root, hierarchy, references).Any(edge => {
            var candidate = Identity(edge.spec);
            return candidate == key || expected is { IsInterface: true } && !hierarchy.ContainsKey(edge.spec.Name)
                && candidate is TypeNode.Fqn { Args.Length: > 0 } from
                && FBoundStarProjectionErasure.ResolveClosedReferenceType(from, references, physicalTypes: true)
                    is { IsInterface: true } actual && expected.IsAssignableFrom(actual);
        });
    }

    internal static ProjectedConstructorPlan Bind(BirTypeParameter[] primary, int captured, Facet[] facets)
    {
        var primaries = primary.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
        foreach (var group in facets.GroupBy(f => f.Primary))
        {
            var p = primaries[group.Key];
            p.Constraints = p.Constraints.OrEmpty().Concat(group.Select(f => f.Bound)).Distinct().ToArray();
        }
        return new(primary, primaries, captured, facets);
    }

    // The stored relation names a bound in the owner's binder; actual rows
    // belong to the selected static MethodDef. Their ordering is immaterial.
    internal static Facet BindFacet(int primary, TypeNode bound, bool native,
        IEnumerable<(TypeNode Physical, TypeNode View)> shippedRows, Func<TypeNode, TypeNode> physical)
    {
        var key = physical(bound);
        TypeNode InOwner(TypeNode t) => CapturedGenericInvocation.Map(t, tv =>
            tv.Scope == "method" ? new TypeNode.Tv("type", tv.I) : tv);
        var rows = shippedRows.ToArray();
        var matched = rows.Single(row => physical(InOwner(row.Physical)).Equals(key));
        return new(primary, InOwner(matched.View), native, rows.Select(row => InOwner(row.View)).ToArray());
    }

    internal static ProjectedConstructorPlan From(BirTypeParameter[] full, int captured, ProjectedClassStorageFrame.InterfaceSlot[] facets)
    {
        var primary = full.Length - facets.Length;
        return Bind(full[..primary], captured, facets.Select((f, i) => new Facet(f.OwnerIndex,
            f.Bound, f.NativeArgument, full[primary + i].Constraints.OrEmpty())).ToArray());
    }

    internal int[] InnerSlots() => Enumerable.Range(Captured, PrimaryArity - Captured)
        .Concat(Facets.Select((f, i) => (f, i)).Where(row => row.f.Primary >= Captured)
            .Select(row => PrimaryArity + row.i)).ToArray();

    internal TypeNode[] Close(TypeNode[] primary, Func<Facet, TypeNode, TypeNode> select)
    {
        if (primary.Length != PrimaryArity)
            throw new InvalidOperationException("Projected constructor arguments do not match the selected entry");
        return primary.Concat(Facets.Select(f => select(f, SupertypeGraph.SubstOwnerTvs(f.Bound, primary)))).ToArray();
    }
}
