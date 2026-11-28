using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class OwnerConstrainedMethodLowering
{
    internal sealed record ConstraintRewrite(TypeNode[] Retained, TypeNode[] Removed);

    // The readers preserve a missing/null constraint vector as null. Only a
    // declared vector (including empty) participates in this physical rewrite.
    internal static ConstraintRewrite[] PlanConstraintRewrite(IReadOnlyList<TypeNode[]> parameters,
        IReadOnlyList<TypeNode[]> ownerParameters, NullableRepresentationFrame frame,
        OwnerConstraintOverrideBounds overrides, ReferenceMetadataIndex references,
        Func<TypeNode, bool> dependsOnOwner, Func<TypeNode, TypeNode> projectedBound,
        Func<TypeNode, TypeNode> physicalClassifier, Func<int, IEnumerable<TypeNode>> nativeProofs)
    {
        var result = new ConstraintRewrite[parameters.Count];
        var inherited = overrides?.Rows ?? Array.Empty<OwnerConstraintOverrideBounds.Row>();
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index] == null) continue;
            var retained = new List<TypeNode>();
            var removed = new List<TypeNode>();
            foreach (var bound in parameters[index])
            {
                bool fromOverride = false;
                foreach (var slot in inherited)
                {
                    if (slot.Index != index || !slot.Bound.Equals(bound)) continue;
                    fromOverride = true;
                    removed.Add(SupertypeGraph.SubstOwnerTvs(projectedBound(slot.Template), slot.OwnerArgs));
                    // Several selected open slots can close to the same source
                    // bound. Each contributes evidence to the implementation.
                    if (physicalClassifier(slot.Template) is { } classifier) retained.Add(classifier);
                    foreach (var consequence in slot.Consequences)
                        if (!retained.Any(row => TypeNodeDataEquality.Equal(row, consequence))) retained.Add(consequence);
                }
                if (fromOverride) continue;
                if (ProjectsBound(index, bound, frame, overrides, references, dependsOnOwner))
                {
                    removed.Add(projectedBound(ProjectMethodVariables(bound, index, frame)));
                    if (physicalClassifier(bound) is { } classifier) retained.Add(classifier);
                    foreach (var consequence in dependsOnOwner(bound)
                        ? IndependentBounds(bound, ownerParameters?.Count ?? 0, index => ownerParameters[index])
                        : Enumerable.Empty<TypeNode>())
                        if (!retained.Any(row => row.Equals(consequence))) retained.Add(consequence);
                }
                else retained.Add(bound);
            }
            foreach (var proof in nativeProofs(index))
                if (!retained.Any(row => row.Equals(proof))) retained.Add(proof);
            result[index] = new(retained.Distinct().Select(BirGraphValue.CopyType).ToArray(),
                removed.Distinct().Select(BirGraphValue.CopyType).ToArray());
        }
        return result;
    }
}
