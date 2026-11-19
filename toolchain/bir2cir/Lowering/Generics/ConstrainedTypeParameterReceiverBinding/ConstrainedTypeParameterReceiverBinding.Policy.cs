using DotKt.Bir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    readonly record struct ConstraintSlot(TypeNode[] Retained, TypeNode[] Erased);

    static IEnumerable<(TypeNode.Fqn Constraint, bool Erased)> ConstraintDeclarations(
        TypeNode.Tv start, IReadOnlyList<ConstraintSlot> owner, IReadOnlyList<ConstraintSlot> method)
        => ConstrainedReceiverPolicy.ConstraintDeclarations(start, owner, method, static slot => (slot.Retained, slot.Erased));

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

}
