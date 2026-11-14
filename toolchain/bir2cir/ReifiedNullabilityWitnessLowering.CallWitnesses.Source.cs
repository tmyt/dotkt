using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    internal static void MaterializeCallWitnesses(BirFileDeclaration root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            // A pending vector is an independently owned recipe. Do not visit
            // it before cloning it into this call's actual argument vector.
            node.VisitOwnChildSlots((field, _, slot) => {
                if (field != PendingWitnessesKey) Walk(slot.Value);
            });
            switch (node)
            {
                case BirCallStatic call when call.NullablePendingWitnesses.HasValue:
                {
                    var pending = call.NullablePendingWitnesses.Value;
                    var arguments = MaterializeWitnessArguments(call.Args, pending);
                    call.Args = arguments;
                    call.NullablePendingWitnesses = default;
                    call.NullableWitnessCount = pending.Length;
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", arguments.Length, pending.Length);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", arguments.Length, pending.Length);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", arguments.Length, pending.Length);
                    break;
                }
                case BirCallInstance call when call.NullablePendingWitnesses.HasValue:
                {
                    var pending = call.NullablePendingWitnesses.Value;
                    var arguments = MaterializeWitnessArguments(call.Args, pending);
                    call.Args = arguments;
                    call.NullablePendingWitnesses = default;
                    call.NullableWitnessCount = pending.Length;
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", arguments.Length, pending.Length);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", arguments.Length, pending.Length);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", arguments.Length, pending.Length);
                    break;
                }
                case BirConstrainedCall call when call.NullablePendingWitnesses.HasValue:
                {
                    var pending = call.NullablePendingWitnesses.Value;
                    var arguments = MaterializeWitnessArguments(call.Args, pending);
                    call.Args = arguments;
                    call.NullablePendingWitnesses = default;
                    call.NullableWitnessCount = pending.Length;
                    call.Sig = FinalizeWitnessVector(call.Sig, "sig", arguments.Length, pending.Length);
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", arguments.Length, pending.Length);
                    break;
                }
                case BirNew call when call.NullablePendingWitnesses.HasValue:
                {
                    var pending = call.NullablePendingWitnesses.Value;
                    var arguments = MaterializeWitnessArguments(call.Args, pending);
                    call.Args = arguments;
                    call.NullablePendingWitnesses = default;
                    call.NullableWitnessCount = pending.Length;
                    call.ArgTypes = FinalizeWitnessVector(call.ArgTypes, "argTypes", arguments.Length, pending.Length);
                    call.MemberSignature = FinalizeWitnessVector(call.MemberSignature, "memberSignature", arguments.Length, pending.Length);
                    break;
                }
            }
        }
        Walk(root);
    }

    static IBirExpression[] MaterializeWitnessArguments(OptionalField<IBirExpression[]> arguments, IBirExpression[] pending)
    {
        if (!arguments.HasValue)
            throw new InvalidOperationException("bir2cir: nullable-sensitive call lost its argument vector");
        return arguments.Value.Concat(pending.Select(witness => witness == null ? null
            : (IBirExpression)((BirGraphValue)witness).DeepClone())).ToArray();
    }

    static OptionalField<TypeNode[]> FinalizeWitnessVector(OptionalField<TypeNode[]> vector, string key,
        int argumentCount, int witnessCount)
    {
        if (!vector.HasValue) return vector;
        var missing = MissingWitnessTypes(key, vector.Value.Length, argumentCount, witnessCount);
        if (missing == 0) return vector;
        return OptionalField<TypeNode[]>.Of(vector.Value.Concat(Enumerable.Range(0, missing)
            .Select(_ => BirGraphValue.CopyType(KotlinRuntimeTypeWitness.Type))).ToArray());
    }
}
