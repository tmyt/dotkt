using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Physical method formals remain in their own binder (!!i). An enclosing !i
// in a constraint must still be substituted from the selected declaring owner.
sealed record ProjectedMethodPlan(BirTypeParameter[] Parameters, int[] FacetSources, int[] AddressOperands)
{
    internal int PrimaryArity => Parameters.Length - FacetSources.Length - AddressOperands.Length;
    internal ProjectedClassStorageFrame.WitnessSlot[] Facets { get; } = FacetSources.Select((source, i) =>
        new ProjectedClassStorageFrame.WitnessSlot(source,
            Parameters[Parameters.Length - FacetSources.Length - AddressOperands.Length + i].Constraints.OrEmpty().Single(), false)).ToArray();

    internal static ProjectedMethodPlan Declare(ProjectedClassStorageFrame frame, BirParameter[] operands)
    {
        var formals = frame.Declare().ToList();
        var facets = frame.Interfaces.Select(slot => slot.OwnerIndex).ToArray();
        var addresses = new List<int>();
        for (int i = 0; i < operands.Length; i++)
        {
            if (SignatureValueTypes.Of(operands[i].Type.Value) is not TypeNode.ByRef reference) continue;
            bool split = false;
            CapturedGenericInvocation.Map(reference.Of, v => { split |= v.Scope == "method" && facets.Contains(v.I); return v; });
            if (!split) continue;
            addresses.Add(i);
            formals.Add(new BirTypeParameter { Name = "$location$" + i });
        }
        return new(formals.ToArray(), facets, addresses.ToArray());
    }

    internal void ApplyAddressFormals(BirParameter[] parameters)
    {
        for (int i = 0; i < AddressOperands.Length; i++)
            parameters[AddressOperands[i]].Type = AddressFormal(i);
    }

    TypeNode AddressFormal(int i) => new TypeNode.ByRef(new TypeNode.Tv("method", PrimaryArity + Facets.Length + i));

    internal TypeNode[] SelectedSignature(TypeNode[] exact)
    {
        var selected = exact.ToArray();
        for (int i = 0; i < AddressOperands.Length; i++) selected[AddressOperands[i]] = AddressFormal(i);
        return selected;
    }

    internal TypeNode[] AddressArguments(TypeNode[] exact, TypeNode[] owner, TypeNode[] actual) =>
        AddressOperands.Select(i => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(
            ((TypeNode.ByRef)SignatureValueTypes.Of(exact[i])).Of, owner, actual)).ToArray();

    internal BirTypeParameter[] CloneParameters() => Parameters.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();

    internal TypeNode[] ForwardTo(ProjectedMethodPlan target, Func<TypeNode,TypeNode> physicalKey)
    {
        var primary = PrimaryArity;
        if (primary != target.PrimaryArity) throw new InvalidOperationException("Projected override primary frames disagree");
        var slots = target.Facets.Select(t => Array.FindIndex(Facets,
            s => s.OwnerIndex == t.OwnerIndex && physicalKey(s.Bound) == physicalKey(t.Bound))).ToArray();
        if (slots.Any(s => s < 0)) throw new InvalidOperationException("Projected override lacks an inherited witness");
        var locations = target.AddressOperands.Select(i => Array.IndexOf(AddressOperands, i)).ToArray();
        if (locations.Any(i => i < 0)) throw new InvalidOperationException("Projected override lacks an inherited address");
        if (slots.Length == Facets.Length && slots.Select((s, i) => s == i).All(s => s)
            && target.AddressOperands.SequenceEqual(AddressOperands)) return null;
        return Enumerable.Range(0, primary).Concat(slots.Select(s => primary + s)).Concat(locations.Select(i => primary + Facets.Length + i))
            .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
    }

    internal ProjectedMethodPlan OnOwner(TypeNode[] arguments)
    {
        var parameters = CloneParameters();
        foreach (var p in parameters)
            if (p.Constraints.HasValue) p.Constraints = p.Constraints.Value.Select(c => SupertypeGraph.SubstOwnerTvs(c, arguments)).ToArray();
        return new(parameters, FacetSources, AddressOperands);
    }

    internal sealed record Application(bool Projected, TypeNode[] Arguments);

    internal Application Close(TypeNode[] actual, TypeNode[] ownerArgs, BirMethodWitness[] candidates,
        BirTypeParameter[] original, NullableRepresentationFrame representations, bool nonvirtualBase,
        Func<TypeNode, TypeNode, TypeNode> lexicalProof, Func<TypeNode, TypeNode, bool> declares,
        Func<TypeNode, TypeNode, bool> proves)
    {
        var arguments = actual.ToArray();
        foreach (var candidate in candidates)
            if (FacetSources.Contains(candidate.Argument.Value))
                arguments[candidate.Argument.Value] = candidate.Primary.Value;
        TypeNode Closed(TypeNode bound) => FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(bound, ownerArgs, arguments);
        TypeNode Facet(ProjectedClassStorageFrame.WitnessSlot slot)
        {
            var bound = Closed(slot.Bound);
            var evidence = candidates.FirstOrDefault(c => c.Argument.Value == slot.OwnerIndex);
            if (evidence != null)
                return proves(actual[slot.OwnerIndex], bound)
                    ? actual[slot.OwnerIndex]
                    : evidence.Interfaces.Value.Any(t => proves(t, bound))
                        ? bound : evidence.Primary.Value;
            return lexicalProof(actual[slot.OwnerIndex], bound);
        }
        if (representations is { } map)
            for (var i = 0; i < actual.Length; i++)
            {
                var source = map.SourcePosition(map.PhysicalSlot(i).SourceIndex);
                if (source == i) continue;
                var bounds = original[i].Constraints.OrEmpty();
                if (bounds.Length != 1) continue;
                var bound = Closed(bounds[0]);
                if (lexicalProof(actual[source], bound) is { } proof) arguments[i] = proof;
                else if (!declares(actual[i], bound) && declares(actual[source], bound)) arguments[i] = actual[source];
            }
        var proofs = Facets.Select(Facet).ToArray();
        return proofs.All(p => p == null) && !nonvirtualBase ? new(false, arguments) :
            new(true, arguments.Concat(proofs.Select((p, i) => p ?? actual[FacetSources[i]])).ToArray());
    }
}
