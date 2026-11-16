using DotKt.Bir;

// Dispatch on a type-parameter receiver is chosen in two physical phases.
// CloseOpenOwners first closes the receiver's direct constraint so inherited
// owner binding can select the member's constructed declaring owner. ApplyAll
// then authors constrained dispatch or the explicit conversion required when
// that source proof was erased from the CLR declaration. Both phases run after
// suspend lowering, over the current declaration-owned physical frames.
//
// Resolved CLR properties have a later narrow phase: only member resolution
// knows whether the selected member is an accessor or a field. Virtual accessors
// can use constrained dispatch; nonvirtual class accessors require conversion.
// A missing/ambiguous construction is never guessed from a physical name.
static partial class ConstrainedTypeParameterReceiverBinding
{
    static TypeNode.Fqn ProjectedForeignConstraint(TypeNode.Fqn source, ReferenceMetadataIndex refs)
    {
        if (source?.Args is not { Length: > 0 } args || !args.Any(ContainsProjection) || refs == null) return null;
        var physical = refs.TryResolveClrOwner(source.Name, out var alias, out _) ? alias : source.Name;
        var candidate = new TypeNode.Fqn(physical, args);
        return ForeignStarProjectionBinding.IsForeignStarType(candidate, refs) ? candidate : null;
    }

    static bool ContainsProjection(TypeNode type) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsProjection),
        TypeNode.Nullable n => ContainsProjection(n.Of),
        TypeNode.Oblivious o => ContainsProjection(o.Of),
        TypeNode.Array a => ContainsProjection(a.Elem),
        TypeNode.ByRef b => ContainsProjection(b.Of),
        TypeNode.Ptr p => ContainsProjection(p.Of),
        TypeNode.Mod m => ContainsProjection(m.M) || ContainsProjection(m.Of),
        TypeNode.Fn fn => ContainsProjection(fn.Ret) || fn.DelegateParams.Any(ContainsProjection)
            || fn.Ctx?.Any(ContainsProjection) == true,
        _ => false,
    };

    // Nullable/platform shells do not change the physical receiver variable.
    static TypeNode Peel(TypeNode type) => type switch {
        TypeNode.Oblivious value => Peel(value.Of),
        TypeNode.Nullable value => Peel(value.Of),
        _ => type,
    };

    // A bare current local generic owner remains open. A bare nongeneric or
    // referenced owner has no missing construction for this pass to recover.
    static TypeNode.Fqn ClosedOwner(TypeNode.Fqn owner, Dictionary<string, int> arity)
        => owner.Args != null ? owner : arity.TryGetValue(owner.Name, out var count) && count > 0 ? null : owner;

    // Constraint routes retain provenance through TV edges. Uniqueness, not
    // first/last encounter, decides whether one source proof selects the owner.
    static TypeNode.Fqn ConstraintAt(TypeNode.Tv variable, string ownerName,
        IReadOnlyList<ConstraintSlot> owner, IReadOnlyList<ConstraintSlot> method, out bool erased)
    {
        erased = false;
        var matches = ConstraintDeclarations(variable, owner, method)
            .Where(candidate => candidate.Constraint.Name == ownerName).ToList();
        if (matches.Count != 1) return null;
        erased = matches[0].Erased;
        return matches[0].Constraint;
    }
}
