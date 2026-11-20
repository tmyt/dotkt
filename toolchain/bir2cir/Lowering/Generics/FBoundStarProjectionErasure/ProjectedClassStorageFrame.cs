using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A frame for the projected implementation only. The exact class keeps its
// original parameter list and constraints. Append slots, so existing type and
// method variables in members never need to be renumbered.
sealed class ProjectedClassStorageFrame
{
    internal sealed record InterfaceSlot(int OwnerIndex, int BoundIndex, TypeNode Bound);
    internal readonly InterfaceSlot[] Interfaces;
    readonly BirTypeParameter[] original;

    ProjectedClassStorageFrame(BirTypeParameter[] original, InterfaceSlot[] interfaces)
        => (this.original, Interfaces) = (original, interfaces);

    internal static ProjectedClassStorageFrame Plan(BirTypeParameter[] parameters,
        Func<int, TypeNode.Fqn, (TypeNode.Fqn Type, string Kind)> physicalBound)
    {
        var slots = new List<InterfaceSlot>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var constraints = parameters[i].Constraints.OrEmpty();
            var physical = constraints.Select((bound, j) => (j, Bound: bound is TypeNode.Fqn named
                ? physicalBound(i, named) : default)).ToArray();
            if (!physical.Any(row => row.Bound.Kind == "class")) continue;
            foreach (var row in physical.Where(row => row.Bound.Kind == "interface"))
                slots.Add(new(i, row.j, row.Bound.Type));
        }
        return slots.Count == 0 ? null : new(parameters, slots.ToArray());
    }

    internal BirTypeParameter[] Declare() => Declare(original, Interfaces);

    internal static BirTypeParameter[] Declare(BirTypeParameter[] original, IReadOnlyList<InterfaceSlot> slots)
    {
        var parameters = original.Select(p => (BirTypeParameter)p.DeepClone()).ToList();
        foreach (var slot in slots)
        {
            var bound = original[slot.OwnerIndex].Constraints.Value[slot.BoundIndex];
            parameters.Add(new BirTypeParameter { Name = "$interface$" + parameters.Count,
                Constraints = new[] { BirGraphValue.CopyType(bound) } });
        }
        foreach (var group in slots.GroupBy(slot => slot.OwnerIndex))
        {
            var indexes = group.Select(slot => slot.BoundIndex).ToHashSet();
            var primary = parameters[group.Key];
            primary.Constraints = primary.Constraints.Value.Where((_, i) => !indexes.Contains(i)).ToArray();
        }
        return parameters.ToArray();
    }

    internal TypeNode[] Close(TypeNode[] primaryArguments) =>
        primaryArguments.Concat(Interfaces.Select(slot => SupertypeGraph.SubstOwnerTvs(slot.Bound, primaryArguments))).ToArray();
}
