using DotKt.Bir;

// Raw inline/default bodies and source snapshots cross an assembly boundary.
// After placement, bind their classifiers to the producer's exact selected
// metadata identity; arguments retain their source order and representation.
static partial class OpaqueCarrierTypeBinding
{
    static TypeNode.Fqn BindSourceClassifier(TypeNode.Fqn type, IReadOnlyDictionary<string, string> localPhysical,
        ReferenceMetadataIndex refs)
    {
        if (localPhysical.TryGetValue(type.Name, out var local)) return type with { Name = local };
        return refs.TryExactPhysicalTypeName(type.Name, type.Args?.Length ?? 0, out var exact)
            && exact != null && exact.Contains('+') ? type with { Name = exact } : type;
    }
}
