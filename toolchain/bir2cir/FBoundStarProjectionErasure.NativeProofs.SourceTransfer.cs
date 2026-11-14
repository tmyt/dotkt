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

        // Both owning source and actual late physical renderers delegate to
        // this one transfer rule. Add owns arity, nullable companion positions,
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
