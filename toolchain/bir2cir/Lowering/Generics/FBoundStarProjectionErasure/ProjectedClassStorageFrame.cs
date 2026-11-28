using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A frame for the projected implementation only. The exact class keeps its
// original parameter list and constraints. Append slots, so existing type and
// method variables in members never need to be renumbered.
sealed class ProjectedClassStorageFrame
{
    internal record WitnessSlot(int OwnerIndex, TypeNode Bound, bool NativeArgument)
    {
        internal TypeNode[] Evidence { get; init; }
        internal bool MaterialReference { get; init; }
        internal BirTypeParameter MaterialFormal { get; init; }
        internal bool Proves(TypeNode constraint, Func<TypeNode,TypeNode,bool> proves) =>
            proves(Bound, constraint) || (Evidence?.Any(b => proves(b, constraint)) ?? false);
    }
    internal sealed record InterfaceSlot(int OwnerIndex, int BoundIndex, TypeNode Bound, TypeNode[] PrimaryBounds = null,
        bool NativeArgument = false) : WitnessSlot(OwnerIndex, Bound, NativeArgument);
    internal readonly InterfaceSlot[] Interfaces;
    readonly BirTypeParameter[] original;
    internal int PrimaryArity => original.Length;

    ProjectedClassStorageFrame(BirTypeParameter[] original, InterfaceSlot[] interfaces)
        => (this.original, Interfaces) = (original, interfaces);

    internal static ProjectedClassStorageFrame Plan(BirTypeParameter[] parameters,
        Func<int, TypeNode.Fqn, (TypeNode.Fqn Type, string Kind)> physicalBound, bool includeWithoutFacets = false,
        Func<int, bool> splitInterfaceConjunction = null)
    {
        var slots = new List<InterfaceSlot>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var constraints = parameters[i].Constraints.OrEmpty();
            var physical = constraints.Select((bound, j) => (j, Bound: bound is TypeNode.Fqn named
                ? physicalBound(i, named) : default)).ToArray();
            // An interface-only constructor can use separate ordinary and
            // interface arguments too. Do not split compiler companion slots.
            if (!physical.Any(row => row.Bound.Kind == "class") &&
                !(splitInterfaceConjunction != null && physical.Where(row => row.Bound.Kind == "interface")
                    .Select(row => row.Bound.Type).Distinct().Skip(1).Any() && splitInterfaceConjunction(i))) continue;
            foreach (var row in physical.Where(row => row.Bound.Kind == "interface"))
                slots.Add(new(i, row.j, row.Bound.Type));
        }
        return slots.Count == 0 && !includeWithoutFacets ? null : new(parameters, slots.ToArray());
    }

    internal BirTypeParameter[] Declare() => Declare(original, Interfaces);
    // Declare puts the designated bound first in the producer's own BIR
    // parameter. This position is never used to interpret CLR metadata rows.
    internal static TypeNode DeclaredFacet(BirTypeParameter generated) => generated.Constraints.Value[0];
    internal ProjectedConstructorPlan Constructor(int captured) =>
        ProjectedConstructorPlan.From(Declare(), captured, Interfaces);

    internal ProjectedClassStorageFrame WithNativeArguments(Func<InterfaceSlot, bool> required) =>
        new(original, Interfaces.Select(s => s with { NativeArgument = required(s) }).ToArray());

    internal ProjectedClassStorageFrame WithMaterialReferences(Func<int, bool> required)
    {
        var material = Interfaces.GroupBy(s => s.OwnerIndex).Where(g => required(g.Key))
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (material.Count == 0) return this;
        return new(original, Interfaces.Select(s => material.TryGetValue(s.OwnerIndex, out var group) && s == group[0]
            ? s with { MaterialReference = true, NativeArgument = true,
                Evidence = (s.Evidence ?? []).Concat(group.Select(f => f.Bound)).Distinct().ToArray() } : s).ToArray());
    }

    internal ProjectedClassStorageFrame WithForwardedPrimaryBounds(Func<InterfaceSlot, IEnumerable<TypeNode>> required)
    {
        var changed = false;
        var next = Interfaces.Select(s => {
            var bounds = s.PrimaryBounds ?? [];
            var added = required(s).Except(bounds).ToArray();
            if (added.Length == 0) return s;
            changed = true;
            return s with { PrimaryBounds = bounds.Concat(added).ToArray() };
        }).ToArray();
        return changed ? new(original, next) : this;
    }

    internal ProjectedClassStorageFrame WithDeclaredProofs(GenericRepresentationPolicy policy, Func<TypeNode,TypeNode> publish = null) =>
        new(original, Interfaces.Select(s => !policy.JoinsNativeWitness(s.Bound) ? s : s with {
            NativeArgument = true,
            Evidence = policy.NativeProofsFromClass(original[s.OwnerIndex].Constraints.OrEmpty())
                .Select(t => publish == null ? t : publish(t)).Distinct().ToArray()
        }).ToArray());

    internal ProjectedClassStorageFrame ForBase(Func<InterfaceSlot, TypeNode[]> requiredOnPrimary)
    {
        var split = Interfaces.Select(s => s with { PrimaryBounds = requiredOnPrimary(s) })
            .Where(s => !s.PrimaryBounds.Contains(s.Bound)).ToArray();
        return split.Length == 0 ? null : new(original, split);
    }

    internal static BirTypeParameter[] Declare(BirTypeParameter[] original, IReadOnlyList<InterfaceSlot> slots)
    {
        var parameters = original.Select(p => (BirTypeParameter)p.DeepClone()).ToList();
        foreach (var slot in slots)
        {
            var bound = original[slot.OwnerIndex].Constraints.Value[slot.BoundIndex];
            // One native argument must prove all of its interface constraints at
            // once. Consumers select the published facet, not the first CLR constraint.
            var bounds = new[] { bound }.Concat(slots.Where(s => slot.NativeArgument && s.NativeArgument && s.OwnerIndex == slot.OwnerIndex)
                .Select(s => original[s.OwnerIndex].Constraints.Value[s.BoundIndex])).Concat(slot.Evidence ?? []).Distinct();
            parameters.Add(new BirTypeParameter { Name = "$interface$" + parameters.Count,
                Constraints = bounds.Select(BirGraphValue.CopyType).ToArray() });
            if (slot.MaterialReference) parameters[^1].SpecialConstraints = new[] { "class" };
        }
        foreach (var group in slots.GroupBy(slot => slot.OwnerIndex))
        {
            var indexes = group.Select(slot => slot.BoundIndex).ToHashSet();
            var primary = parameters[group.Key];
            primary.Constraints = primary.Constraints.Value.Where((_, i) => !indexes.Contains(i))
                .Concat(group.SelectMany(slot => slot.PrimaryBounds ?? [])).Distinct().ToArray();
        }
        return parameters.ToArray();
    }

}
