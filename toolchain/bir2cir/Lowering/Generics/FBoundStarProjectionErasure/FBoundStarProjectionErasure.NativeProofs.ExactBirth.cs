using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        // This producer edge is an independent occurrence in the SAME generic
        // frame, unlike an existential-interface slot transfer. Copy the exact
        // captured rows, including owner-dependent proofs and empty demand rows.
        // In particular do not repoint the source method-selection dictionary.
        internal void TransferExactSourceProofs(BirGraphValue origin, BirGraphValue projection)
        {
            var source = bindings.GetCapturedSource(origin);
            var target = bindings.GetSource(projection);
            if (ReferenceEquals(source, target)) throw new InvalidOperationException("An independent proof birth reused its source binding");
            if (!rows.TryGetValue(source, out var proofs)) return;
            if (SourceParameters(origin).Count != SourceParameters(projection).Count || rows.ContainsKey(target))
                throw new InvalidOperationException("An exact proof birth changed its declaration frame or was admitted twice");
            rows.Add(target, proofs.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(BirGraphValue.CopyType).ToList()));
        }
    }
}
