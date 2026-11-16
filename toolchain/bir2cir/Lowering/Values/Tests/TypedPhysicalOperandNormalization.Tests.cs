using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypedPhysicalOperandNormalization
{
    internal static void SelfTest()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var boolean = new TypeNode.Fqn("System.Boolean");
        BirConstant Literal(int value) => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(value) };
        BirValueBlock Boundary(TypeNode type) => new()
        {
            Type = type, Stmts = new IBirStatement[] { new BirTry { Body = Array.Empty<IBirStatement>() } },
            Result = new BirConstant { Type = type, Value = new BirLiteral.IntegerLiteral(1) },
        };
        BirCallStatic Call(params IBirExpression[] arguments) => new()
        {
            Method = "Consume", Args = arguments, Ret = integer,
        };
        var getter = BirMemberReference.FromPhysical(new MemberRefNode("method", "System.Private.CoreLib",
            new TypeNode.Fqn("System.Type"), "get_IsValueType", 0, boolean, "instance", []));
        void Normalize(BirFileDeclaration root)
        {
            new Normalizer(type => type.Name == "System.Int32",
                TypedPhysicalValueCoercion.ResultTypeOracle(new[] { root }), getter)
                .Walk(root, new Dictionary<string, TypeNode>(StringComparer.Ordinal));
        }
        BirFunctionDeclaration Method(params IBirStatement[] body) => new()
        {
            Name = "Probe", Params = new[] { new BirParameter { Name = "value", Type = integer } },
            Ret = integer, Body = body,
        };
        BirFileDeclaration File(BirFunctionDeclaration method) => new() { FileClass = "ProbeFile", Methods = new[] { method } };

        var orderedCall = Call(new BirLocal { Name = "value" }, Boundary(integer));
        var ordered = Method(new BirExpressionStatement { Expr = orderedCall });
        Normalize(File(ordered));
        var orderedBody = ordered.Body.Value;
        if (orderedBody.Length != 3
            || orderedBody[0] is not BirVariable { Init.HasValue: true } first
            || first.Init.Value is not BirLocal { Name.Value: "value" }
            || orderedBody[1] is not BirVariable { Init.HasValue: true } second
            || second.Init.Value is not BirValueBlock
            || orderedCall.Args.Value.Any(argument => argument is not BirLocal))
            throw new InvalidOperationException("Typed operand normalization lost prefix order or local snapshots");

        var conditionCall = Call(new BirLocal { Name = "value" }, Boundary(integer));
        var loop = new BirWhile { Cond = conditionCall, Body = Array.Empty<IBirStatement>() };
        var guarded = Method(loop);
        Normalize(File(guarded));
        if (guarded.Body.Value.Length != 1 || loop.Cond.Value is not BirValueBlock { Stmts.HasValue: true } site
            || site.Stmts.Value.Length != 2 || !ReferenceEquals(site.Result.Value, conditionCall))
            throw new InvalidOperationException("Typed operand prefixes escaped a repeated condition site");

        var arrayType = new TypeNode.Array(integer);
        var locationCall = Call(new BirArrayGet { Array = new BirLocal { Name = "array", Sty = arrayType },
            Index = Literal(0), Elem = integer }, Boundary(integer));
        locationCall.ArgTypes = new TypeNode[] { new TypeNode.ByRef(integer), integer };
        var addresses = Method(new BirExpressionStatement { Expr = locationCall });
        Normalize(File(addresses));
        if (addresses.Body.Value[0] is not BirVariable { Type.HasValue: true, Init.HasValue: true } address
            || address.Type.Value is not TypeNode.ByRef || address.Init.Value is not BirByRefOf
            || locationCall.Args.Value[0] is not BirByRefLoad)
            throw new InvalidOperationException("Typed operand normalization copied a managed location instead of its address");

        var variable = new TypeNode.Tv("method", 0);
        var genericCall = new BirConstrainedCall
        {
            RecvType = variable, Recv = new BirLocal { Name = "receiver", Sty = variable },
            Args = new IBirExpression[] { Boundary(integer) }, Sig = new TypeNode[] { integer }, CalleeRet = integer,
        };
        var generic = Method(new BirExpressionStatement { Expr = genericCall });
        generic.Params = new[] { new BirParameter { Name = "receiver", Type = variable } };
        Normalize(File(generic));
        if (generic.Body.Value[0] is not BirVariable { Init.IsPresent: false }
            || generic.Body.Value[1] is not BirVariable { Init.HasValue: true } selected
            || selected.Init.Value is not BirConditional { Type.HasValue: true } split
            || split.Type.Value is not TypeNode.ByRef
            || split.Cond.Value is not BirNativeInstance { MemberRef.HasValue: true }
            || genericCall.Recv.Value is not BirByRefLoad)
            throw new InvalidOperationException("Typed generic receiver lost its reference/value address split");

        var constructorCall = Boundary(integer);
        var constructor = new BirConstructor
        {
            Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>(),
            PreStmts = new IBirStatement[] { new BirVariable { Name = "value", Type = integer, Init = Literal(2) } },
            BaseArgs = new IBirExpression[] { new BirLocal { Name = "value" }, constructorCall },
            DelegationSig = new TypeNode[] { integer, integer },
        };
        var constructed = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Constructed", Kind = "class", Ctors = new[] { constructor } },
        } };
        Normalize(constructed);
        if (constructor.PreStmts.Value.Length != 3
            || constructor.PreStmts.Value[1] is not BirVariable { Init.HasValue: true } prior
            || prior.Init.Value is not BirLocal { Name.Value: "value" }
            || constructor.BaseArgs.Value.Any(argument => argument is not BirLocal))
            throw new InvalidOperationException("Typed delegation operands were not evaluated before the implicit this prefix");

        var deferred = new BirNewClosure
        {
            ClosureType = new TypeNode.Fqn("Deferred"), Captures = Array.Empty<IBirExpression>(),
            SynthClass = new BirClosureDefinition { Name = "Deferred", Params = Array.Empty<BirLambdaParameter>(),
                Ret = integer, Body = new IBirStatement[] { new BirReturn { Value = Literal(9) } } },
        };
        var deferredCall = Call(deferred, Literal(1));
        var deferredMethod = Method(new BirExpressionStatement { Expr = deferredCall });
        Normalize(File(deferredMethod));
        if (deferredMethod.Body.Value.Length != 1 || !ReferenceEquals(deferredCall.Args.Value[0], deferred))
            throw new InvalidOperationException("A deferred body was mistaken for a caller-side stack transfer");

        BirGraphValue[] locations = [new BirLocal { Name = "x" }, new BirThis(),
            new BirArrayGet { Array = Literal(0), Index = Literal(0), Elem = integer },
            new BirFieldGet { Member = "field" }, new BirFieldGet { Member = "accessor" },
            new BirPropertyGet { Member = "field" }, new BirPropertyGet { Member = "accessor" },
            new BirByRefLoad { Local = "pointer", Elem = integer }, Literal(1)];
        foreach (var value in locations)
            if (CallEvalLowering.IsLvalueFormer(value) != CallEvalLowering.IsLvalueFormer(value.ToDom()))
                throw new InvalidOperationException("Typed/DOM location classifiers disagree");
        BirGraphValue[] receivers = [new BirConstrainedCall(),
            new BirNativeInstance { Type = integer, Recv = Literal(0) },
            new BirPropertySet { Type = integer, Recv = Literal(0) },
            new BirCallInstance { Recv = new BirLocal { Name = "r", Sty = variable } },
            new BirCallInstance { Recv = new BirLocal { Name = "r", Sty = new TypeNode.Fqn("System.Object") } }];
        foreach (var value in receivers)
            if (CallEvalLowering.ReceiverNeedsAddress(value, type => type.Name == integer.Name)
                != CallEvalLowering.ReceiverNeedsAddress(value.ToDom(), type => type.Name == integer.Name))
                throw new InvalidOperationException("Typed/DOM address-receiver classifiers disagree");
        Console.WriteLine("[typed physical operands] self-test OK (ordered values, guarded sites, exact addresses, generic receivers, delegation, deferred frames, shared classifiers)");
    }
}
