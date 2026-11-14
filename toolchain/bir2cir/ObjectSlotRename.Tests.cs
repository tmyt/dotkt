using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ObjectSlotRename
{
    internal static void SelfTest()
    {
        void Idempotent(BirGraphValue actual)
        {
            var before = actual.DeepClone();
            if (!Apply(actual).StructurallyEquals(before))
                throw new InvalidOperationException("Owning object-slot normalization is not idempotent");
        }
        BirCallInstance Call(string method, bool anySlot = true, bool superCall = false) => new() {
            OwnerType = new TypeNode.Fqn("probe.Value"), Method = method, AnySlot = anySlot, Super = superCall,
            DeclarationId = "source-slot", Recv = new BirLocal { Name = "receiver" },
            Args = method is "equals" or "Equals" ? new IBirExpression[] { new BirLocal { Name = "argument" } }
                : Array.Empty<IBirExpression>(), Sty = new TypeNode.Tv("method", 0),
        };
        foreach (var (source, physical) in new[] { ("toString", "ToString"), ("hashCode", "GetHashCode"),
                     ("equals", "Equals"), ("ToString", "ToString"), ("GetHashCode", "GetHashCode"),
                     ("Equals", "Equals"), ("unrelated", "unrelated") })
        {
            var unmarked = Call(source, anySlot: false);
            Apply(unmarked);
            if (unmarked.Method.OrDefault() != source || unmarked.DeclarationId.OrDefault() != "source-slot")
                throw new InvalidOperationException("An unmarked call was mistaken for a semantic Any slot");
            Idempotent(unmarked);
            var declaration = new BirFunctionDeclaration { Name = source, ObjectOverride = true,
                DeclarationId = "override", Pos = new BirDeclarationPosition { F = "source.kt", L = 7, C = 3 },
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = Array.Empty<BirParameter>() };
            Apply(declaration);
            if (declaration.Name.OrDefault() != physical || declaration.DeclarationId.OrDefault() != "override"
                || declaration.Pos.Value.L.OrDefault() != 7 || declaration.TypeParams.Value[0].Name.OrDefault() != "T")
                throw new InvalidOperationException("Object override normalization changed declaration facts");
            Idempotent(declaration);
            var staticCall = (BirCallStatic)Apply(new BirCallStatic { Method = source, AnySlot = true,
                DeclarationId = "source-static", Args = Array.Empty<IBirExpression>() });
            var bound = (BirNewBoundDelegate)Apply(new BirNewBoundDelegate { Method = source, AnySlot = true,
                DeclarationId = "source-delegate", Recv = new BirLocal { Name = "receiver" },
                TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 1) } });
            if (staticCall.Method.OrDefault() != physical || bound.Method.OrDefault() != physical
                || staticCall.DeclarationId.IsPresent || bound.DeclarationId.IsPresent
                || staticCall.AnySlot.OrDefault() != (physical is "Equals" or "GetHashCode")
                || bound.AnySlot.OrDefault() != (physical is "Equals" or "GetHashCode")
                || bound.TypeArgs.Value[0] is not TypeNode.Tv)
                throw new InvalidOperationException("Static/bound object-slot normalization changed exact call facts");
            Idempotent(staticCall); Idempotent(bound);
            var objectCall = Apply(new BirObjectMethod { Method = source, Recv = new BirLocal { Name = "receiver" },
                Arg = new BirLocal { Name = "argument" } });
            if (physical is "Equals" or "GetHashCode")
            {
                if (objectCall is not BirCallStatic helper || helper.Method.OrDefault()
                    != (physical == "Equals" ? "kotlinInstanceEquals" : "kotlinInstanceHashCode"))
                    throw new InvalidOperationException("Object method did not lower semantic equality/hash");
            }
            else if (objectCall is not BirObjectMethod direct || direct.Method.OrDefault() != physical)
                throw new InvalidOperationException("Object method did not retain its canonical direct slot");
            Idempotent(objectCall);
        }
        foreach (var superCall in new[] { false, true })
        foreach (var virtualCall in new[] { default(OptionalField<bool>), OptionalField<bool>.Null, true, false })
        foreach (var owner in new[] { "probe.Value", "kotlin.Array", "kotlin.IntArray" })
        {
            var call = Call("toString", superCall: superCall);
            call.Virtual = virtualCall;
            call.OwnerType = new TypeNode.Fqn(owner);
            Apply(call);
            var retained = owner != "probe.Value" || !superCall && (!virtualCall.HasValue || virtualCall.Value);
            if (call.Method.OrDefault() != "ToString" || call.DeclarationId.IsPresent
                || call.AnySlot.IsPresent != retained || call.AnySlot.OrDefault() != retained
                || call.Super.OrDefault() != superCall || !call.Virtual.Equals(virtualCall)
                || call.Sty.OrDefault() is not TypeNode.Tv)
                throw new InvalidOperationException("Virtual/native-array ToString lost dispatch or presence facts");
            Idempotent(call);
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
            var normalized = Apply(new BirObjectEquals { Lhs = receiver, Rhs = new BirConstant {
                Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = nullValue } });
            if (normalized is not BirBinaryOperation { Op.HasValue: true } comparison || comparison.Op.Value != "==="
                || comparison.Lhs.OrDefault() is not BirCast left || !ReferenceEquals(left.E.OrDefault(), receiver))
                throw new InvalidOperationException("Owning null guard introduced virtual equality or duplicated evaluation");
            Idempotent(normalized);
        }
        var preserved = (BirCallInstance)Apply(Call("equals", superCall: true));
        if (!preserved.AnySlot.OrDefault() || preserved.DeclarationId.IsPresent)
            throw new InvalidOperationException("Representation entry lost super intent or retained a consumed declaration identity");
        var late = (BirCallInstance)KotlinEqualityLowering.Apply(preserved);
        if (!ReferenceEquals(late, preserved) || late.AnySlot.IsPresent || !late.Super.OrDefault()
            || late.Method.OrDefault() != "Equals")
            throw new InvalidOperationException("Late equality must consume super intent without changing its nonvirtual target");
        var payloadCall = Call("equals");
        var payload = new BirInlinePayload { Body = new IBirStatement[] { new BirReturn { Value = payloadCall } } };
        if (!ReferenceEquals(Apply(payload), payload) || payloadCall.Method.OrDefault() != "equals"
            || payloadCall.DeclarationId.OrDefault() != "source-slot")
            throw new InvalidOperationException("Object normalization entered an opaque executable payload");
        var body = new BirFunctionDeclaration { Name = "function", Body = new IBirStatement[] {
            new BirReturn { Value = Call("hashCode") },
        } };
        Apply(body);
        if (((BirReturn)body.Body.Value[0]).Value.OrDefault() is not BirCallStatic nested
            || nested.Method.OrDefault() != "kotlinInstanceHashCode")
            throw new InvalidOperationException("Object normalization did not enter an ordinary function body");
        Idempotent(body);
        Console.WriteLine("[owning object slots] self-test OK (slot facts, virtual/super policy, null guards, operand transfer, opaque payloads)");
    }
}
