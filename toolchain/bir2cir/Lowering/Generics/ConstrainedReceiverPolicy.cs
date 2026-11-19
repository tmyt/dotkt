using DotKt.Bir;

// Each phase supplies its own constraint frame; only decisions about its scalar entries are shared.
static class ConstrainedReceiverPolicy
{
    // Nullable/platform shells do not change the physical receiver variable.
    internal static TypeNode Peel(TypeNode type) => type switch {
        TypeNode.Oblivious value => Peel(value.Of),
        TypeNode.Nullable value => Peel(value.Of),
        _ => type,
    };

    internal static IEnumerable<(TypeNode.Fqn Constraint, bool Erased)> ConstraintDeclarations<TSlot>(
        TypeNode.Tv start, IReadOnlyList<TSlot> owner, IReadOnlyList<TSlot> method,
        Func<TSlot, (TypeNode[] Retained, TypeNode[] Erased)> bounds)
    {
        var result = new List<(TypeNode.Fqn Constraint, bool Erased)>();
        var path = new HashSet<(string Scope, int Index)>();
        void Walk(TypeNode.Tv variable, bool erased)
        {
            var key = (variable.Scope, variable.I);
            if (!path.Add(key)) return;
            try
            {
                var frame = variable.Scope == "type" ? owner : variable.Scope == "method" ? method : null;
                if (frame == null || variable.I < 0 || variable.I >= frame.Count) return;
                void Follow(IEnumerable<TypeNode> constraints, bool removed)
                {
                    foreach (var constraint in constraints)
                        switch (Peel(constraint))
                        {
                            case TypeNode.Fqn named: result.Add((named, erased || removed)); break;
                            case TypeNode.Tv next: Walk(next, erased || removed); break;
                        }
                }
                var (retained, removedBounds) = bounds(frame[variable.I]);
                Follow(retained, false);
                Follow(removedBounds, true);
            }
            finally { path.Remove(key); }
        }
        Walk(start, false);
        return result.Distinct();
    }

    internal static void RecordLocal(Dictionary<string, TypeNode> locals, HashSet<string> ambiguous, string name, TypeNode type)
    {
        if (name == null || type == null) return;
        if (locals.TryGetValue(name, out var previous) && previous != type)
        { ambiguous.Add(name); locals.Remove(name); }
        else if (!ambiguous.Contains(name)) locals[name] = type;
    }
}
