using DotKt.Bir;
using DotKt.Bir.Lowering;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    internal sealed partial class NativeProofPlan
    {
        // The source is already a captured declaration. Only the explicitly
        // authored physical projection is admitted here; neither occurrence
        // changes the original local selection/eligibility inventories.
        internal void TransferIndependentMethodProofs(BirFunctionDeclaration source, BirFunctionDeclaration projection)
        {
            var original = bindings.GetCapturedSource(source);
            var target = bindings.GetSource(projection);
            TransferIndependentMethodProofRows(Rows(original), target, projection.DeclarationId.OrDefault());
        }

        // The producer calls this only after attaching the actual descriptor
        // to the emitted bridge's clrInterfaceImpls. A candidate slot used for
        // selection is not an owning declaration and must not be admitted.
        internal void TransferIndependentMethodProofs(BirFunctionDeclaration source, BirMethodImpl projection)
        {
            var original = bindings.GetCapturedSource(source);
            var target = bindings.GetSource(projection);
            // MethodImpl carries its own typeParams, but no declaration ID or
            // nullable method frame. Do not borrow either from its bridge.
            TransferIndependentMethodProofRows(Rows(original), target, null);
        }

        // Both actual method and attached MethodImpl producers use this one
        // transfer rule. Add owns arity, nullable companion positions,
        // duplicate admission and independent copies of the proof TypeNodes.
        void TransferIndependentMethodProofRows(IReadOnlyDictionary<int, List<TypeNode>> sourceRows,
            DeclarationBinding target, string identity)
        {
            foreach (var (index, proofs) in sourceRows.ToArray())
                foreach (var proof in proofs.Where(proof => !ContainsOwnerTv(proof)).ToArray())
                    Add(target, index, proof);
            if (identity != null) methods[identity] = target;
        }
    }
}
