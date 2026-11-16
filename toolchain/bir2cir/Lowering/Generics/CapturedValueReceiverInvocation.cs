using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Reflection closes only the selected method frame. The native invocation
// consumes the caller's original managed address through a stateless adapter.
static partial class CapturedValueReceiverInvocation
{
    static readonly TypeNode Object = new TypeNode.Fqn("object");
    static readonly TypeNode Values = new TypeNode.Array(Object);
    internal sealed record Plan(BirInterfaceDefinition Contract, BirClassDefinition Adapter, IBirStatement[] FactoryBody,
        TypeNode.Fqn CallerContract, TypeNode Receiver, TypeNode ContractReceiver, TypeNode Return);

    internal static Plan Create(string name, string lexicalOwner, BirTypeParameter[] ownerFrame, BirTypeParameter[] methodFrame,
        BirTypeParameter[] fullFrame, TypeNode.Fqn receiver, TypeNode.Fqn liftedReceiver, BirGraphValue forward, bool returnsVoid)
    {
        var selectedMethodArity = fullFrame.Length - ownerFrame.Length - methodFrame.Length;
        TypeNode ClassFrame(TypeNode type) => CapturedGenericInvocation.Map(type, variable => new TypeNode.Tv("type", variable.I));
        TypeNode ContractFrame(TypeNode type) => CapturedGenericInvocation.Map(type,
            variable => new TypeNode.Tv("type", variable.Scope == "type" ? variable.I : ownerFrame.Length + variable.I));
        var callerArguments = Enumerable.Range(0, ownerFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("type", index))
            .Concat(Enumerable.Range(0, methodFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index))).ToArray();
        var contractName = name + "$receiver";
        var adapterName = name + "$adapter";
        var contractReceiver = ContractFrame(receiver);
        var result = returnsVoid ? new TypeNode.Fqn("void") : Object;
        BirFunctionDeclaration Method(TypeNode receiverType, bool abstractMethod, IBirStatement[] body) => new() {
            Name = "Invoke", Static = false, Vis = "public", Virtual = true, Abstract = abstractMethod, Override = false,
            TypeParams = System.Array.Empty<BirTypeParameter>(), Ret = result, Body = body, Attrs = System.Array.Empty<BirAttribute>(),
            Params = new[] { new BirParameter { Name = "receiver", Type = new TypeNode.ByRef(receiverType) },
                new BirParameter { Name = "values", Type = Values } },
        };
        var contractFrame = ownerFrame.Concat(methodFrame).Select(parameter => {
            var clone = CapturedGenericInvocation.CloneParameter(parameter, ContractFrame);
            clone.Variance = default;
            return clone;
        }).ToArray();
        var contract = new BirInterfaceDefinition {
            Name = contractName, Kind = "interface", Generated = true, Vis = "internal", TypeParams = contractFrame,
            Interfaces = System.Array.Empty<TypeNode>(), Fields = System.Array.Empty<BirFieldDeclaration>(),
            Ctors = System.Array.Empty<BirConstructor>(), Methods = new[] { Method(contractReceiver, true, []) }, Attrs = System.Array.Empty<BirAttribute>(),
        };
        // Only constructed owner/call-site types move to the adapter's class
        // frame. The selected native MemberRef parameter/return signature does not.
        var invocation = CapturedGenericInvocation.AdapterForward(forward, liftedReceiver, ClassFrame);
        IBirStatement[] adapterBody = returnsVoid
            ? new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)invocation) } }
            : new IBirStatement[] { new BirReturn { Value = CapturedGenericInvocation.Cast(Object, (IBirExpression)invocation) } };
        var adapter = new BirClassDefinition {
            Name = adapterName, Kind = "class", Generated = true, Vis = "internal", NestedIn = lexicalOwner,
            TypeParams = fullFrame.Select(parameter => CapturedGenericInvocation.CloneParameter(parameter, ClassFrame)).ToArray(),
            Interfaces = new TypeNode[] { new TypeNode.Fqn(contractName, callerArguments.Length == 0 ? null
                : Enumerable.Range(0, callerArguments.Length).Select(index => (TypeNode)new TypeNode.Tv("type", selectedMethodArity + index)).ToArray()) },
            Fields = System.Array.Empty<BirFieldDeclaration>(), Attrs = System.Array.Empty<BirAttribute>(),
            Ctors = new[] { new BirConstructor { Vis = "public", Params = System.Array.Empty<BirParameter>(),
                BaseArgs = OptionalField<IBirExpression[]>.Null, Body = System.Array.Empty<IBirStatement>() } },
            Methods = new[] { Method(ClassFrame(liftedReceiver), false, adapterBody) },
        };
        var factoryBody = new IBirStatement[] { new BirReturn { Value = new BirNew {
            Type = new TypeNode.Fqn(adapterName, Enumerable.Range(0, fullFrame.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray()),
            Args = System.Array.Empty<IBirExpression>(), ArgTypes = System.Array.Empty<TypeNode>(), LocalCtorIndex = 0,
        } } };
        return new(contract, adapter, factoryBody, new TypeNode.Fqn(contractName, callerArguments.Length == 0 ? null : callerArguments),
            receiver, contractReceiver, result);
    }

    internal static IBirExpression Invoke(Plan plan, IBirExpression receiver, IBirExpression factory, string prefix, int valueArgumentIndex)
    {
        var pointer = prefix + "$address";
        var values = prefix + "$values";
        var helper = prefix + "$helper";
        var statements = new List<IBirStatement>();
        var location = (IBirExpression)((BirGraphValue)receiver).DeepClone();
        if (!TypedCallEvalLowering.IsLvalueFormer(location))
        {
            // An ordinary expression, even a ref-returning call consumed as a
            // value, snapshots before argument effects. Only a real lvalue keeps storage.
            var value = prefix + "$receiver";
            statements.Add(Variable(value, plan.Receiver, location));
            location = Local(value);
        }
        statements.Add(Variable(pointer, new TypeNode.ByRef(plan.Receiver), new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(location) }));
        var factoryArguments = factory switch {
            BirNativeStatic call => call.Args.Value, BirCallStatic call => call.Args.Value,
            _ => throw new InvalidOperationException("Captured receiver factory is not the selected runtime helper"),
        };
        var valueArray = (IBirExpression)((BirGraphValue)factoryArguments[valueArgumentIndex]).DeepClone();
        factoryArguments[valueArgumentIndex] = Local(values);
        statements.Add(Variable(values, Values, valueArray));
        statements.Add(Variable(helper, plan.CallerContract, CapturedGenericInvocation.Cast(plan.CallerContract, factory)));
        return new BirValueBlock {
            Type = plan.Return, Stmts = statements.ToArray(), Result = new BirCallInstance {
                OwnerType = plan.CallerContract, Method = "Invoke", Virtual = true, Recv = Local(helper),
                Sig = new TypeNode[] { new TypeNode.ByRef(plan.ContractReceiver), Values },
                Ret = plan.Return, CalleeRet = plan.Return, Args = new IBirExpression[] { Local(pointer), Local(values) },
            },
        };
    }

    static BirVariable Variable(string name, TypeNode type, IBirExpression init) => new() {
        Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(init),
    };
    static BirLocal Local(string name) => new() { Name = name };
}
