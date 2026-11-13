using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ObjectSlotRename
{
    internal static void SelfTest()
    {
        void Parity(BirGraphValue input)
        {
            var expected = input.ToDom();
            Apply(expected);
            var actual = Apply(input.DeepClone());
            if (!JsonNode.DeepEquals(actual.ToDom(), expected))
                throw new InvalidOperationException("Owning object-slot normalization differs from the remaining DOM consumer");
            if (!JsonNode.DeepEquals(Apply(actual).ToDom(), expected))
                throw new InvalidOperationException("Owning object-slot normalization is not idempotent");
        }
        BirCallInstance Call(string method, bool anySlot = true, bool superCall = false) => new() {
            OwnerType = new TypeNode.Fqn("probe.Value"), Method = method, AnySlot = anySlot, Super = superCall,
            DeclarationId = "source-slot", Recv = new BirLocal { Name = "receiver" },
            Args = new IBirExpression[] { new BirLocal { Name = "argument" } },
            Sty = new TypeNode.Tv("method", 0),
        };
        foreach (var method in new[] { "toString", "hashCode", "equals", "ToString", "GetHashCode", "Equals", "unrelated" })
        {
            foreach (var anySlot in new[] { false, true })
            foreach (var superCall in new[] { false, true })
            foreach (var virtualCall in new[] { default(OptionalField<bool>), OptionalField<bool>.Null, true, false })
            foreach (var owner in new[] { "probe.Value", "kotlin.Array", "kotlin.IntArray" })
            {
                var call = Call(method, anySlot, superCall);
                call.Virtual = virtualCall;
                call.OwnerType = new TypeNode.Fqn(owner);
                Parity(call);
                var renamed = (BirCallInstance)call.DeepClone();
                renamed.Method = RenameName(method);
                if (AnySlotRebind.IsVirtualToString(renamed) != AnySlotRebind.IsVirtualToString(renamed.ToDom()))
                    throw new InvalidOperationException("Virtual Any slot policy differs across graph representations");
            }
            Parity(new BirCallStatic { Method = method, AnySlot = true, DeclarationId = "source-static", Args = Array.Empty<IBirExpression>() });
            Parity(new BirNewBoundDelegate { Method = method, AnySlot = true, DeclarationId = "source-delegate",
                Recv = new BirLocal { Name = "receiver" }, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) } });
            Parity(new BirObjectMethod { Method = method, Recv = new BirLocal { Name = "receiver" }, Arg = new BirLocal { Name = "argument" } });
            Parity(new BirFunctionDeclaration { Name = method, ObjectOverride = true,
                DeclarationId = "override", Pos = new BirDeclarationPosition { F = "source.kt", L = 7, C = 3 },
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = Array.Empty<BirParameter>() });
        }
        var receiver = new BirCallStatic { Method = "sideEffect", Args = Array.Empty<IBirExpression>() };
        var argument = new BirLocal { Name = "argument" };
        var equality = Call("equals");
        equality.Recv = receiver;
        equality.Args = new IBirExpression[] { argument };
        var lowered = (BirCallStatic)Apply(equality);
        if (!ReferenceEquals(lowered.Args.Value[0], receiver) || !ReferenceEquals(lowered.Args.Value[1], argument)
            || lowered.Method.OrDefault() != "kotlinInstanceEquals" || lowered.DeclarationId.IsPresent)
            throw new InvalidOperationException("Owning equality did not transfer its once-evaluated operands in order");
        foreach (var nullValue in new[] { OptionalField<BirLiteral>.Null,
                     OptionalField<BirLiteral>.Of(new BirLiteral.NullLiteral()) })
        {
            var guard = new BirObjectEquals { Lhs = receiver, Rhs = new BirConstant {
                Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = nullValue } };
            Parity(guard);
            if (Apply(guard) is not BirBinaryOperation { Op.HasValue: true } comparison || comparison.Op.Value != "==="
                || comparison.Lhs.OrDefault() is not BirCast left || !ReferenceEquals(left.E.OrDefault(), receiver))
                throw new InvalidOperationException("Owning null guard introduced virtual equality or duplicated evaluation");
        }
        var preserved = (BirCallInstance)Apply(Call("equals", superCall: true));
        if (!preserved.AnySlot.OrDefault() || preserved.DeclarationId.IsPresent)
            throw new InvalidOperationException("Representation entry lost super intent or retained a consumed declaration identity");
        var lateDom = preserved.ToDom();
        KotlinEqualityLowering.Apply(lateDom);
        var late = (BirCallInstance)KotlinEqualityLowering.Apply(preserved);
        if (late.AnySlot.IsPresent || !late.Super.OrDefault() || !JsonNode.DeepEquals(late.ToDom(), lateDom))
            throw new InvalidOperationException("Late equality must consume super intent without changing its nonvirtual target");
        var payloadCall = Call("equals");
        var payload = new BirInlinePayload { Body = new IBirStatement[] { new BirReturn { Value = payloadCall } } };
        if (!ReferenceEquals(Apply(payload), payload) || payloadCall.Method.OrDefault() != "equals"
            || payloadCall.DeclarationId.OrDefault() != "source-slot")
            throw new InvalidOperationException("Object normalization entered an opaque executable payload");
        var body = new BirFunctionDeclaration { Name = "function", Body = new IBirStatement[] {
            new BirReturn { Value = Call("hashCode") },
        } };
        Parity(body);
        Console.WriteLine("[owning object slots] self-test OK (slot facts, virtual/super policy, null guards, operand transfer, opaque payloads)");
    }
}
