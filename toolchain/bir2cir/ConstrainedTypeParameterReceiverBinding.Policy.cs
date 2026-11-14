using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    readonly record struct ConstraintSlot(TypeNode[] Retained, TypeNode[] Erased);

    static ConstraintSlot[] ConstraintFrame(JsonArray parameters) => parameters == null ? Array.Empty<ConstraintSlot>()
        : parameters.Select(parameter => parameter is JsonObject declaration
            ? new ConstraintSlot((declaration["constraints"] as JsonArray)?.Select(TypeJson.Read).ToArray() ?? Array.Empty<TypeNode>(),
                (declaration[FBoundStarProjectionErasure.ErasedInnerConstraintKey] as JsonArray)?.Select(TypeJson.Read).ToArray() ?? Array.Empty<TypeNode>())
            : new ConstraintSlot(Array.Empty<TypeNode>(), Array.Empty<TypeNode>())).ToArray();

    static IEnumerable<(TypeNode.Fqn Constraint, bool Erased)> ConstraintDeclarations(
        TypeNode.Tv start, IReadOnlyList<ConstraintSlot> owner, IReadOnlyList<ConstraintSlot> method)
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
                Follow(frame[variable.I].Retained, false);
                Follow(frame[variable.I].Erased, true);
            }
            finally { path.Remove(key); }
        }
        Walk(start, false);
        return result.Distinct();
    }

    static bool ConstraintDispatchWasErased(TypeNode.Tv variable, string owner,
        IReadOnlyList<ConstraintSlot> ownerFrame, IReadOnlyList<ConstraintSlot> methodFrame)
    {
        var matches = ConstraintDeclarations(variable, ownerFrame, methodFrame)
            .Where(candidate => candidate.Constraint.Name == owner).ToArray();
        return matches.Length == 1 && matches[0].Erased;
    }

    static IEnumerable<(int Index, TypeNode[] Bounds)> ProjectedSourceBounds(IReadOnlyDictionary<int, TypeNode[]> bounds,
        int count, int offset, NullableRepresentationFrame frame)
    {
        foreach (var (sourceIndex, sourceBounds) in bounds)
            if (sourceIndex >= 0 && (frame == null || sourceIndex < frame.SourceArity)
                && offset + (frame?.SourcePosition(sourceIndex) ?? sourceIndex) < count
                && sourceBounds.Any(bound => bound != null && ContainsProjection(bound)))
                yield return (offset + (frame?.SourcePosition(sourceIndex) ?? sourceIndex),
                    sourceBounds.Select(bound => TypedExecutableFrames.RewriteType(bound, variable =>
                        frame != null && variable.Scope == "type"
                            ? new TypeNode.Tv("type", frame.SourcePosition(variable.I)) : null)).ToArray());
    }

    static void RecordLocal(Dictionary<string, TypeNode> locals, HashSet<string> ambiguous, string name, TypeNode type)
    {
        if (name == null || type == null) return;
        if (locals.TryGetValue(name, out var previous) && previous != type)
        { ambiguous.Add(name); locals.Remove(name); }
        else if (!ambiguous.Contains(name)) locals[name] = type;
    }
}
