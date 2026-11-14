using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedValueReceiverInvocation
{
    internal static void SelfTest()
    {
        var selected = new TypeNode.Tv("method", 0);
        var caller = new TypeNode.Tv("method", 1);
        var receiver = new TypeNode.Fqn("Native.Value", new TypeNode[] { new TypeNode.Tv("type", 0) });
        var liftedReceiver = new TypeNode.Fqn(receiver.Name, new TypeNode[] { caller });
        var ownerFrame = new[] { new BirTypeParameter { Name = "Owner" } };
        var fullFrame = new[] { new BirTypeParameter { Name = "Selected" }, new BirTypeParameter { Name = "Owner" } };
        var declared = new TypeNode.Fqn("Key", new TypeNode[] { selected });
        var member = new BirMemberReference { Kind = "method", Assembly = "Native", Name = "Invoke", GenericArity = 1,
            DeclaringType = liftedReceiver, ParameterTypes = new TypeNode[] { declared }, ReturnType = selected };
        var forward = new BirGenericInstance { Type = liftedReceiver, Method = "Invoke", TypeArgs = new TypeNode[] { selected },
            Ret = selected, ResolvedMemberReturn = selected, MemberRef = member,
            Recv = new BirLocal { Name = "p0" }, Args = new IBirExpression[] { new BirLocal { Name = "p1" } } };
        var plan = Create("Owner.helper", "Owner", ownerFrame, [], fullFrame, receiver, liftedReceiver, forward, false);
        var adapterCall = (BirGenericInstance)((BirCast)((BirReturn)plan.Adapter.Methods.Value[0].Body.Value[0]).Value.Value).E.Value;
        Check(plan.Adapter.Fields.Value.Length == 0 && plan.Contract.TypeParams.Value.Length == 1
            && plan.Adapter.TypeParams.Value.Length == 2
            && ((TypeNode.Fqn)plan.Adapter.Interfaces.Value[0]).Args[0] == new TypeNode.Tv("type", 1)
            && plan.Contract.Methods.Value[0].Params.Value[0].Type.Value is TypeNode.ByRef
            && adapterCall.Recv.Value is BirByRefLoad
            && ((TypeNode.Fqn)adapterCall.MemberRef.Value.DeclaringType.Value).Args[0] == new TypeNode.Tv("type", 1)
            && adapterCall.TypeArgs.Value[0] == new TypeNode.Tv("type", 0)
            && ((TypeNode.Fqn)adapterCall.MemberRef.Value.ParameterTypes.Value[0]).Args[0] == selected
            && adapterCall.MemberRef.Value.ReturnType.Value == selected
            && !ReferenceEquals(adapterCall.MemberRef.Value, member),
            "adapter must use a managed receiver and class call-site frame without remapping the native declaration signature");

        BirCallStatic Factory() => new() { Method = "factory", Ret = Object, Args = new IBirExpression[] {
            new BirNewArray { Elem = Object, Elems = new IBirExpression[] {
                new BirConstant { Type = Object, Value = new BirLiteral.NullLiteral() },
                new BirCallStatic { Method = "argumentEffect", Ret = Object, Args = Array.Empty<IBirExpression>() },
            } },
        } };
        var factory = Factory();
        var actual = (BirValueBlock)Invoke(plan, new BirLocal { Name = "location" }, factory, "capture", 0);
        var statements = actual.Stmts.Value.Cast<BirVariable>().ToArray();
        Check(statements.Length == 3 && statements[0].Init.Value is BirByRefOf { Inner.Value: BirLocal { Name.Value: "location" } }
            && statements[1].Init.Value is BirNewArray values && values.Elems.Value[1] is BirCallStatic { Method.Value: "argumentEffect" }
            && statements[2].Init.Value is BirCast { E.Value: BirCallStatic }
            && factory.Args.Value[0] is BirLocal { Name.Value: "capture$values" }
            && actual.Result.Value is BirCallInstance { Virtual.Value: true } call
            && call.Args.Value[0] is BirLocal { Name.Value: "capture$address" },
            "original address must be captured before argument effects and stateless factory invocation");
        var snapshot = (BirValueBlock)Invoke(plan, new BirCallStatic { Method = "valueReceiver", Ret = receiver, Args = Array.Empty<IBirExpression>() },
            Factory(), "snapshot", 0);
        var first = (BirVariable)snapshot.Stmts.Value[0];
        var second = (BirVariable)snapshot.Stmts.Value[1];
        Check(snapshot.Stmts.Value.Length == 4 && first.Init.Value is BirCallStatic { Method.Value: "valueReceiver" }
            && second.Init.Value is BirByRefOf { Inner.Value: BirLocal { Name.Value: "snapshot$receiver" } },
            "non-location receiver must snapshot once before capturing its address and evaluating arguments");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed captured receiver: " + message); }
}
