using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    internal static void FinalizeCallSignatures(BirGraphValue root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            // Scalar address/transport facts are not graph children; only the
            // same maintained structural children participate in this walk.
            node.VisitOwnChildSlots((_, _, slot) => Walk(slot.Value));
            switch (node)
            {
                case BirCallStatic call when call.NullableWitnessCount.OrDefault() > 0:
                    var staticCount = WitnessArgumentCount(call.Args);
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", staticCount, call.NullableWitnessCount.Value);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", staticCount, call.NullableWitnessCount.Value);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", staticCount, call.NullableWitnessCount.Value);
                    call.NullableWitnessCount = default;
                    break;
                case BirCallInstance call when call.NullableWitnessCount.OrDefault() > 0:
                    var instanceCount = WitnessArgumentCount(call.Args);
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", instanceCount, call.NullableWitnessCount.Value);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", instanceCount, call.NullableWitnessCount.Value);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", instanceCount, call.NullableWitnessCount.Value);
                    call.NullableWitnessCount = default;
                    break;
                case BirConstrainedCall call when call.NullableWitnessCount.OrDefault() > 0:
                    var constrainedCount = WitnessArgumentCount(call.Args);
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", constrainedCount, call.NullableWitnessCount.Value);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", constrainedCount, call.NullableWitnessCount.Value);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", constrainedCount, call.NullableWitnessCount.Value);
                    call.NullableWitnessCount = default;
                    break;
                case BirNew call when call.NullableWitnessCount.OrDefault() > 0:
                    var constructionCount = WitnessArgumentCount(call.Args);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", constructionCount, call.NullableWitnessCount.Value);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", constructionCount, call.NullableWitnessCount.Value);
                    call.NullableWitnessCount = default;
                    break;
            }
        }
        Walk(root);
    }

    static int WitnessArgumentCount(OptionalField<IBirExpression[]> arguments) => arguments.HasValue
        ? arguments.Value.Length
        : throw new InvalidOperationException("bir2cir: nullable-sensitive call lost its argument vector");
}
