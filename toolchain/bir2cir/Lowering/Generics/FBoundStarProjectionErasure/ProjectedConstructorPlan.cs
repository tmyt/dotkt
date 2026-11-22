using DotKt.Bir;
using DotKt.Bir.Lowering;

// The static factory's primary formals have the exact owner's physical order.
// Facets follow in entry order; their bounds are expressed in that owner binder.
sealed record ProjectedConstructorPlan(int PrimaryArity, int Captured, ProjectedConstructorPlan.Facet[] Facets)
{
    internal sealed record Facet(int Primary, TypeNode Bound, bool NativeArgument);

    // The stored relation names a bound in the owner's binder; actual rows
    // belong to the selected static MethodDef. Their ordering is immaterial.
    internal static Facet BindFacet(int primary, TypeNode bound, bool native,
        IEnumerable<(TypeNode Physical, TypeNode View)> shippedRows, Func<TypeNode, TypeNode> physical)
    {
        var key = physical(bound);
        TypeNode InOwner(TypeNode t) => CapturedGenericInvocation.Map(t, tv =>
            tv.Scope == "method" ? new TypeNode.Tv("type", tv.I) : tv);
        var matched = shippedRows.Single(row => physical(InOwner(row.Physical)).Equals(key));
        return new(primary, InOwner(matched.View), native);
    }

    internal static ProjectedConstructorPlan From(int primary, int captured, ProjectedClassStorageFrame.InterfaceSlot[] facets) =>
        new(primary, captured, facets.Select(f => new Facet(f.OwnerIndex, f.Bound, f.NativeArgument)).ToArray());

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
