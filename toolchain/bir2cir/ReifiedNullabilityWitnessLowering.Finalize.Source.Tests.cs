using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    internal static void FinalSignatureSourceSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var value = new BirLocal { Name = "value" };
        var witness = new BirLocal { Name = "witness" };
        var constructor = new BirNew { Type = new TypeNode.Fqn("Generated"), NullableWitnessCount = 1,
            Args = new IBirExpression[] { value, witness }, ArgTypes = new TypeNode[] { integer },
            MemberSignature = OptionalField<TypeNode[]>.Null };
        var constrained = new BirConstrainedCall { Method = "consume", NullableWitnessCount = 1,
            Args = new IBirExpression[] { constructor }, Sig = Array.Empty<TypeNode>(),
            MemberSignature = Array.Empty<TypeNode>() };
        var instance = new BirCallInstance { Method = "consume", NullableWitnessCount = 1,
            Recv = OptionalField<IBirExpression>.Of(constrained), Args = new IBirExpression[] { new BirLocal { Name = "instanceWitness" } },
            Sig = OptionalField<TypeNode[]>.Null, MemberSignature = Array.Empty<TypeNode>() };
        var complete = new TypeNode[] { KotlinRuntimeTypeWitness.Type };
        var call = new BirCallStatic { Method = "consume", NullableWitnessCount = 1,
            Args = new IBirExpression[] { instance }, Sig = complete, ArgTypes = Array.Empty<TypeNode>() };
        var saved = new byte[] { 2, 1, 3 };
        var method = new BirFunctionDeclaration { Name = "caller", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(call) } }, InlineBir = saved };
        var root = new BirFileDeclaration { FileClass = "WitnessSignatures", Methods = new[] { method } };
        var arguments = call.Args.Value;
        FinalizeCallSignatures(root);
        SourceCheck(!call.NullableWitnessCount.IsPresent && !instance.NullableWitnessCount.IsPresent
            && !constrained.NullableWitnessCount.IsPresent && !constructor.NullableWitnessCount.IsPresent,
            "final signature completion consumes the actual four call counts");
        SourceCheck(ReferenceEquals(call.Sig.Value, complete) && call.ArgTypes.Value.Single() == KotlinRuntimeTypeWitness.Type
            && instance.Sig.IsNull && !instance.ArgTypes.IsPresent
            && instance.MemberSignature.Value.Single() == KotlinRuntimeTypeWitness.Type
            && constrained.Sig.Value.Single() == KotlinRuntimeTypeWitness.Type
            && constrained.MemberSignature.Value.Single() == KotlinRuntimeTypeWitness.Type
            && constructor.ArgTypes.Value.SequenceEqual(new TypeNode[] { integer, KotlinRuntimeTypeWitness.Type })
            && constructor.MemberSignature.IsNull, "presence, completed-vector identity and shared witness-vector policy");
        SourceCheck(ReferenceEquals(call.Args.Value, arguments) && ReferenceEquals(call.Args.Value[0], instance)
            && ReferenceEquals(instance.Recv.Value, constrained) && ReferenceEquals(constrained.Args.Value[0], constructor)
            && ReferenceEquals(constructor.Args.Value[0], value) && ReferenceEquals(constructor.Args.Value[1], witness)
            && ReferenceEquals(root.Methods.Value[0], method) && ReferenceEquals(method.InlineBir.Value, saved),
            "signature completion must not clone/move an executable or declaration occurrence");
        // The same scalar validation used by materialization refuses a missing
        // ordinary argument rather than filling it with a witness type.
        var refused = false;
        try { _ = MissingWitnessTypes("sig", 0, 2, 1); }
        catch (InvalidOperationException) { refused = true; }
        SourceCheck(refused, "hidden witnesses do not supply ordinary Kotlin argument types");
        Console.WriteLine("[source final witness signatures] presence, exact counts, postorder and owning identity: OK");
    }
}
