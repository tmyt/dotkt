using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignStarProjectionBinding
{
    internal static void SelfTestSource()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning foreign projection: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var list = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { new TypeNode.Star() });
        var exactList = new TypeNode.Fqn(list.Name, new[] { String });
        var actualReceiver = new BirCallStatic { Owner = new TypeNode.Fqn("probe.Calls"), Method = "receiver",
            Sig = Array.Empty<TypeNode>(), Ret = AnyN, Args = Array.Empty<IBirExpression>() };
        var classifier = new BirIsInstance { Type = new TypeNode.Nullable(list), E = Expr(actualReceiver) };
        var count = new BirNativeInstance { Type = list, Method = "get_Count", Sig = Array.Empty<TypeNode>(), Ret = Int,
            Recv = Expr(new BirLocal { Name = "preferred" }) };
        var property = new BirPropertyGet { Type = list, Name = "Count", Ret = Int,
            Recv = Expr(new BirLocal { Name = "preferred" }),
            _foreignDeclarationSignature = SourceTypeVector.Create(Array.Empty<TypeNode>()) };
        var statements = new IBirStatement[] {
            new BirVariable { Name = "preferred", Type = list, Init = Expr(new BirCast { Type = exactList, E = Expr(new BirLocal { Name = "input" }) }) },
            SourceStatement(classifier), SourceStatement(count), SourceStatement(property),
        };
        var method = new BirFunctionDeclaration { Name = "foreign", Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = statements };
        var file = new BirFileDeclaration { FileClass = "probe.Foreign", Methods = new[] { method }, Types = Array.Empty<IBirTypeDeclaration>() };
        var materializedCallbacks = 0;
        var appends = ApplyAll(new[] { file }, new Dictionary<string, string>(), refs, null, (type, _) => type,
            types => { materializedCallbacks++; Require(types.Count == 0, "ordinary reflection dispatch synthesized a delegate"); });
        Require(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, statements)
            && materializedCallbacks == 1 && appends.Count == 1 && ReferenceEquals(appends[0].File, file)
            && appends[0].AppendedTypes.Count == 0 && UsedRuntimeFallback,
            "initial mutation replaced a declaration/statement vector or failed its actual append handoff");
        var loweredClassifier = (BirValueBlock)((BirExpressionStatement)statements[1]).Expr.Value;
        Require(loweredClassifier.Stmts.Value is [BirVariable capture] && ReferenceEquals(capture.Init.Value, actualReceiver)
            && loweredClassifier.Result.Value is BirConditional { Then.Value: BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } } },
            "nullable classifier did not capture its original operand once before the null branch");
        Require(refs.TryForeignStarMethod(list, "get_Count", null, 0, Array.Empty<TypeNode>(),
            out _, out _, out var token, out _, out _, out _, out _, out _), "actual framework Count was not selectable");
        foreach (var statement in statements.Skip(2))
        {
            var expression = ((BirExpressionStatement)statement).Expr.Value;
            var runtime = (BirCallStatic)((BirCast)expression).E.Value;
            Require(runtime.Method.Value == "starProjectionInvoke" && runtime.Args.Value.Length == 9
                && runtime.Args.Value[2] is BirClassReference { Type.Value: TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] } }
                && runtime.Args.Value[3] is BirConstant { Value.Value: BirLiteral.IntegerLiteral number } && number.Value == token
                && runtime.Sig.Value.Length == 9 && runtime.Sig.Value[6] is TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.String" } },
                "reflection descriptor lost its selected native token, exact preferred view, or runtime signature");
        }
        Require(_closedViewHints["preferred"] == exactList && _bindProjectedHelpers == false,
            "typed initial capture changed the closed-view hint or enabled late-only helper binding");

        // Verify the pointer/clone/write-back order directly using the actual
        // public field of a shipped CLR generic value type.
        var tuple = new TypeNode.Fqn("System.ValueTuple", new TypeNode[] { new TypeNode.Star() });
        var fieldReceiver = new BirLocal { Name = "tuple", Sty = tuple };
        var fieldValue = new BirConstant { Type = String, Value = new BirLiteral.TextLiteral("updated") };
        var field = new BirPropertySet { Type = tuple, Name = "Item1", Recv = Expr(fieldReceiver), Value = Expr(fieldValue),
            _foreignDeclarationSignature = SourceTypeVector.Create(new TypeNode[] { new TypeNode.Tv("type", 0) }) };
        var loweredField = (BirValueBlock)RewriteSourceCall(field, refs, new(ReferenceEqualityComparer.Instance));
        Require(loweredField.Stmts.Value is [BirVariable pointer, BirVariable box, BirExpressionStatement write, BirExpressionStatement store]
            && pointer.Init.Value is BirByRefOf address && ReferenceEquals(address.Inner.Value, fieldReceiver)
            && box.Init.Value is BirCallStatic { Method.Value: "starProjectionCloneValue" }
            && write.Expr.Value is BirCallStatic set && set.Method.Value == "starProjectionSetField"
            && ReferenceEquals(set.Args.Value[5], fieldValue) && store.Expr.Value is BirByRefStore,
            "value receiver field-set lost address capture/clone/mutate/write-back order or operand identity");

        // Adapter builder operands and their enclosing frame stay owned. The
        // current typed materializer consumes the exact construction slot.
        var target = new TypeNode.Fn(false, String, new TypeNode[] { new TypeNode.Tv("method", 0) });
        var adapters = new HashSet<BirClosureDefinition>(ReferenceEqualityComparer.Instance);
        var delegateValue = new BirLocal { Name = "foreignDelegate" };
        var adapter = SourceDelegateAdapter(delegateValue, target, adapters);
        var ingredients = adapter.SynthClass.Value;
        Require(ReferenceEquals(adapter.Captures.Value.Single(), delegateValue)
            && adapter.TypeArgs.Value is [TypeNode.Tv { Scope: "method", I: 0 }]
            && ingredients.TypeParams.Value is [BirTypeParameter { Name.Value: "T0" }]
            && ingredients.Params.Value.Single().Type.Value is TypeNode.Tv { Scope: "method", I: 0 }
            && adapters.Contains(ingredients), "delegate adapter lost caller-frame correspondence or capture ownership");
        var holder = SourceStatement(adapter);
        var adapterFile = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Name = "adapt",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "foreignDelegate", Type = AnyN } },
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { holder } } },
            Types = Array.Empty<IBirTypeDeclaration>() };
        var materialized = ClosureSynthesis.ApplyMaterialized(adapterFile, new[] {
            new ClosureSynthesis.MaterializedOccurrence(new BirGraphSlot(() => (BirGraphValue)holder.Expr.Value,
                value => holder.Expr = Expr((IBirExpression)value)), new("adapt", null, null)),
        }, refs);
        Require(materialized.AppendedTypes.Count == 1 && ReferenceEquals(adapterFile.Types.Value.Single(), materialized.AppendedTypes[0])
            && ReferenceEquals(holder.Expr.Value, adapter) && !adapter.SynthClass.IsPresent,
            "materialized delegate did not append its actual class or update its actual owning occurrence");

        var nullableDelegate = new BirLocal { Name = "nullableDelegate" };
        var nullableTarget = new TypeNode.Nullable(target);
        var nullableAdapter = (BirValueBlock)SourceDelegateResult(nullableDelegate, nullableTarget, target, adapters);
        Require(nullableAdapter.Stmts.Value is [BirVariable nullableCapture]
            && ReferenceEquals(nullableCapture.Init.Value, nullableDelegate)
            && nullableAdapter.Result.Value is BirConditional conditional
            && conditional.Then.Value is BirConstant { Type.Value: TypeNode.Nullable { Of: TypeNode.Fn } }
            && conditional.Else.Value is BirNewClosure nullChecked
            && nullChecked.Captures.Value.Single() is BirLocal read && read.Name.Value == nullableCapture.Name.Value,
            "nullable delegate adaptation duplicated its operand or discarded its nullable function surface");

        // Initial reset and late continuation are the same shared state policy.
        var adapterNumber = _nextDelegateAdapter;
        BeginBinding(null, new(), resetUsage: false);
        Require(UsedRuntimeFallback && _nextDelegateAdapter == adapterNumber && _bindProjectedHelpers,
            "late entry reset initial runtime usage or adapter-name allocation");
        BeginBinding(null, new(), resetUsage: true);
        Require(!UsedRuntimeFallback && _nextDelegateAdapter == 0 && !_bindProjectedHelpers,
            "a new initial entry retained preceding module usage/counters");
        Console.WriteLine("[owning foreign projection] self-test OK (exact native tokens/hints, nullable identity, value write-back, typed adapter materialization, reset/late clock)");
    }
}
