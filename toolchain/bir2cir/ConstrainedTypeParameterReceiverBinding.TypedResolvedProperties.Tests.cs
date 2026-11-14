using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void ResolvedPropertiesSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var contract = new TypeNode.Fqn("Probe.Contract", Array.Empty<TypeNode>());
        var integer = new TypeNode.Fqn("System.Int32");
        BirPropertyGet Getter(string dispatch = "callvirt") => new() {
            Type = contract, Member = "accessor", Accessor = "get_Value", Dispatch = dispatch,
            Recv = new BirLocal { Name = "receiver", Sty = new TypeNode.Fqn("System.Object") },
            Ret = integer, Sty = integer,
            MemberRef = new BirMemberReference { ParameterTypes = Array.Empty<TypeNode>() },
        };
        var getter = Getter();
        var value = new BirLocal { Name = "argument", Sty = integer };
        var setter = new BirPropertySet {
            Type = contract, Member = "accessor", Accessor = "set_Item", Dispatch = "callvirt",
            Recv = new BirLocal { Name = "receiver" }, Value = value,
            MemberRef = new BirMemberReference { ParameterTypes = new TypeNode[] { contract, integer } },
        };
        var nonvirtual = Getter("call");
        var erased = Getter(); erased._erasedConstraintDispatch = true;
        var explicitRetained = Getter(); explicitRetained._erasedConstraintDispatch = false;
        var field = Getter(); field.Member = "field";
        var statik = Getter(); statik.Static = true;
        var expressions = new IBirExpression[] { getter, setter, nonvirtual, erased, explicitRetained, field, statik };
        var statements = expressions.Select(expression => new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of(expression),
        }).ToArray();
        var method = new BirFunctionDeclaration {
            Name = "use", Params = new[] { new BirParameter { Name = "receiver", Type = variable } },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = Array.Empty<TypeNode>() } },
            Body = statements,
            _ownerConstraintDispatchBounds = new OwnerConstraintDispatchBounds(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { contract } }),
        };
        var body = method.Body.Value;
        var file = new BirFileDeclaration { FileClass = "Probe", Methods = new[] { method } };
        ApplyResolvedProperties(file);
        // The declared local type, not its broad expression stamp, selects the
        // physical generic receiver. Explicit false is different from absence.
        if (statements[0].Expr.Value is not BirPropertyGet { Recv.Value: BirCast }
            || statements[1].Expr.Value is not BirPropertySet { Recv.Value: BirCast }
            || nonvirtual.Recv.Value is not BirCast || erased.Recv.Value is not BirCast
            || statements[4].Expr.Value is not BirConstrainedCall retained
            || !ReferenceEquals(method.Body.Value, body) || !ReferenceEquals(file.Methods.Value[0], method)
            || !ReferenceEquals(statements[5].Expr.Value, field) || !ReferenceEquals(statements[6].Expr.Value, statik)
            || method._ownerConstraintDispatchBounds.IsPresent || method.TypeParams.Value[0].ErasedInnerConstraints.IsPresent
            || getter._erasedConstraintDispatch.IsPresent || erased._erasedConstraintDispatch.IsPresent
            || retained.Sty.IsPresent)
            throw new InvalidOperationException("Resolved property dispatch lost explicit provenance, local types or declaration ownership");

        // A genuine retained route projects the already-selected exact accessor,
        // preserving each operand occurrence once and the exact selected row.
        var retainedGet = Getter();
        var retainedSet = (BirPropertySet)setter.DeepClone(); retainedSet.Recv = new BirLocal { Name = "receiver" };
        retainedSet.Value = value;
        var selectedSet = retainedSet.MemberRef.Value;
        method._ownerConstraintDispatchBounds = default;
        method.TypeParams.Value[0].Constraints = new TypeNode[] { contract };
        method.Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = retainedGet }, new BirExpressionStatement { Expr = retainedSet },
        };
        ApplyResolvedProperties(file);
        var getCall = (BirConstrainedCall)((BirExpressionStatement)method.Body.Value[0]).Expr.Value;
        var setCall = (BirConstrainedCall)((BirExpressionStatement)method.Body.Value[1]).Expr.Value;
        if (!ReferenceEquals(getCall.MemberRef.Value, retainedGet.MemberRef.Value)
            || !ReferenceEquals(getCall.Recv.Value, retainedGet.Recv.Value) || getCall.Args.Value.Length != 0
            || !ReferenceEquals(setCall.MemberRef.Value, selectedSet) || setCall.Args.Value.Length != 1
            || !ReferenceEquals(setCall.Args.Value[0], value) || setCall.Sig.Value.Length != 1
            || setCall.Sig.Value[0] != integer || getCall.Ret.Value != integer || getCall.Sty.IsPresent)
            throw new InvalidOperationException("Resolved constrained accessor re-selected a member or duplicated setter operands");

        // The final owner lookup is local to this file and counts captured
        // parameters too. A round-trip header is never live dispatch evidence.
        var bare = Getter(); bare.Type = new TypeNode.Fqn("Probe.Generic");
        method.Body = new IBirStatement[] { new BirExpressionStatement { Expr = bare } };
        var owner = new BirClassDefinition { Name = "Probe.Generic", CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } } };
        file.Types = new IBirTypeDeclaration[] { owner };
        ApplyResolvedProperties(file);
        if (!ReferenceEquals(((BirExpressionStatement)method.Body.Value[0]).Expr.Value, bare))
            throw new InvalidOperationException("Bare local generic owner was treated as closed");
        file.Types = Array.Empty<IBirTypeDeclaration>();
        ApplyResolvedProperties(file);
        if (((BirExpressionStatement)method.Body.Value[0]).Expr.Value is not BirConstrainedCall)
            throw new InvalidOperationException("Resolved property arity snapshot leaked from an earlier file");

        var ambiguousGet = Getter();
        method.Body = new IBirStatement[] {
            new BirVariable { Name = "receiver", Type = integer },
            new BirExpressionStatement { Expr = ambiguousGet },
        };
        ApplyResolvedProperties(file);
        if (!ReferenceEquals(((BirExpressionStatement)method.Body.Value[1]).Expr.Value, ambiguousGet))
            throw new InvalidOperationException("Whole-body ambiguous local declarations selected a guessed constraint frame");

        var headerGet = Getter(); headerGet.Recv = new BirLocal { Name = "receiver" };
        var sourceBounds = new SourceTypeFacts(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { new TypeNode.Fqn("Probe.Contract", new TypeNode[] { new TypeNode.Star() }) },
        });
        var typeMethod = new BirFunctionDeclaration { Name = "typeUse",
            Params = new[] { new BirParameter { Name = "receiver", Type = new TypeNode.Tv("type", 0) } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = headerGet } },
        };
        owner.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        owner.CapturedTypeParams = default;
        owner.Methods = new[] { typeMethod };
        owner.RoundtripSupertypesPre = sourceBounds;
        file.Types = new IBirTypeDeclaration[] { owner }; file.Methods = Array.Empty<BirFunctionDeclaration>();
        ApplyResolvedProperties(file);
        if (((BirExpressionStatement)typeMethod.Body.Value[0]).Expr.Value is not BirConstrainedCall
            || !ReferenceEquals(owner.RoundtripSupertypesPre.Value, sourceBounds))
            throw new InvalidOperationException("Frozen round-trip header was reinterpreted as live dispatch evidence");
        var liveGet = Getter(); liveGet.Recv = new BirLocal { Name = "receiver" };
        typeMethod.Body = new IBirStatement[] { new BirExpressionStatement { Expr = liveGet } };
        owner.KotlinSupertypesPre = sourceBounds;
        ApplyResolvedProperties(file);
        if (liveGet.Recv.Value is not BirCast)
            throw new InvalidOperationException("Live projected source bounds lost their erased dispatch route");

        var markerCall = new BirCallInstance { _erasedConstraintDispatch = false };
        var markerNative = new BirNativeInstance { _erasedConstraintDispatch = true };
        var markerGeneric = new BirGenericInstance { _erasedConstraintDispatch = OptionalField<bool>.Null };
        var opaque = new BirInlinePayload { Body = new IBirStatement[] { new BirExpressionStatement { Expr = new BirCallInstance { _erasedConstraintDispatch = true } } } };
        foreach (var node in new BirGraphValue[] { markerCall, markerNative, markerGeneric, opaque }) ClearResolvedConstraintFacts(node);
        if (markerCall._erasedConstraintDispatch.IsPresent || markerNative._erasedConstraintDispatch.IsPresent
            || markerGeneric._erasedConstraintDispatch.IsPresent
            || !((BirCallInstance)((BirExpressionStatement)opaque.Body.Value[0]).Expr.Value)._erasedConstraintDispatch.HasValue)
            throw new InvalidOperationException("Final dispatch cleanup missed an operation or opened an opaque payload");

        var raw = "{\"0\":[{\"t\":\"tv\",\"scope\":\"method\",\"i\":1}],\"1\":[{\"t\":\"fqn\",\"name\":\"Probe.Contract\"}]}";
        var dispatchBounds = OwnerConstraintDispatchBounds.ReadFrozenText(raw);
        var slots = ResolvedConstraintFrame(new[] { new BirTypeParameter(), new BirTypeParameter() }, dispatchBounds, null, null);
        if (dispatchBounds.FrozenText != raw || !ConstraintDispatchWasErased(variable, "Probe.Contract", Array.Empty<ConstraintSlot>(), slots))
            throw new InvalidOperationException("Physical dispatch carrier lost raw-map encoding or transitive erased-edge provenance");
        Console.WriteLine("[typed resolved properties] self-test OK (exact accessor, provenance, once-evaluation, local arities and complete cleanup)");
    }
}
