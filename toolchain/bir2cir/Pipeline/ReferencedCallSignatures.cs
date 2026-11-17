using DotKt.Bir;
using DotKt.Bir.Lowering;

// Preserved Kotlin call descriptors for ONE module, captured before erasure.
// This owner can write only its snapshot token on calls; it does not select
// declarations, rewrite physical signatures, or own executable graphs.
// Clones may share a token, including across files: releasing one root's tokens
// must not invalidate another root's saved facts. Dispose ends the module clock.
sealed partial class ReferencedCallSignatures : IDisposable
{
    readonly Dictionary<int, TypeNode[]> snapshots = new();
    int nextId;

    internal void Capture(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirCallStatic { Sig.HasValue: true } call && !call.DotktKotlinSigId.HasValue)
            call.DotktKotlinSigId = Remember(call.Sig.Value);
        node.VisitChildren(Capture);
    }

    int Remember(IEnumerable<TypeNode> signature)
    {
        var id = ++nextId;
        snapshots.Add(id, signature.Select(BirGraphValue.CopyType).ToArray());
        return id;
    }

    internal TypeNode[] Read(BirCallStatic call) => call.DotktKotlinSigId.HasValue
        ? snapshots.GetValueOrDefault(call.DotktKotlinSigId.Value) : null;

    // Cold lowering gets this capability, not access to the table or resolver.
    internal void ProjectColdCall(BirCallStatic source, BirCallStatic target, TypeNode completion)
    {
        if (source.DotktKotlinSigId.HasValue && snapshots.TryGetValue(source.DotktKotlinSigId.Value, out var signature))
            target.DotktKotlinSigId = Remember(signature.Append(completion));
    }

    internal void ReleaseMarkers(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirCallStatic call) call.DotktKotlinSigId = default;
        node.VisitChildren(ReleaseMarkers);
    }

    public void Dispose() => snapshots.Clear();
}
