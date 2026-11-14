using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StringCharSequenceBridge
{
    internal static void SelfTestSource()
    {
        var saved = (_adapterEmitted, _fired, _refs, _retLambdas, _counter, _delegateAdapterCounter);
        try { Run(); }
        finally { (_adapterEmitted, _fired, _refs, _retLambdas, _counter, _delegateAdapterCounter) = saved; }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning String/CharSequence bridge: " + message);
        }
        static TypeNode T(string name) => new TypeNode.Fqn(name);
        static BirLocal Local(string name) => new() { Name = name };
        static BirConstant Text(string text) => new() { Type = T("kotlin.String"), Value = new BirLiteral.TextLiteral(text) };
        static BirExpressionStatement Statement(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        static BirCallStatic Call(IBirExpression value, TypeNode target) => new() {
            Method = "consume", Sig = new[] { target }, Args = new[] { value },
        };
        static bool Adapted(IBirExpression value) => value is BirNew { Type.Value: TypeNode.Fqn { Name: Adapter }, Args.Value: [_] };

        void Run()
        {
            _adapterEmitted = false;
            var original = Text("first");
            var first = new BirVariable { Name = "text", Type = T("kotlin.String"), Init = original };
            var after = Call(Local("text"), T(CharSeq));
            var before = Call(Local("later"), T(CharSeq));
            var late = new BirVariable { Name = "later", Type = T("kotlin.String"), Init = Text("later") };
            var resultCall = Call(Local("inner"), T(CharSeq));
            var block = new BirValueBlock { Stmts = new IBirStatement[] {
                new BirVariable { Name = "inner", Type = T("kotlin.String"), Init = Text("inside") },
            }, Result = resultCall };
            var nullableValue = new BirCallStatic { Method = "readNullable", Ret = new TypeNode.Nullable(T("kotlin.String")), Args = Array.Empty<IBirExpression>() };
            var nullable = Call(nullableValue, new TypeNode.Nullable(T(CharSeq)));
            var custom = Local("custom");
            var join = new BirConditional { Type = T(CharSeq), Cond = new BirConstant { Type = T("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) },
                Then = Text("string"), Else = custom };
            var physicalValue = Text("physical");
            var physical = new BirCallStatic { Method = "physical", Sig = new[] { T("kotlin.String") }, ShapeTypes = new[] { T(CharSeq) }, Args = new IBirExpression[] { physicalValue } };
            var shape = new[] { T(CharSeq) };
            var witness = Local("witness");
            var source = new BirCallStatic { Method = "source", ShapeTypes = shape, DeclarationId = "selected",
                Args = new IBirExpression[] { Text("source"), witness } };
            var store = new BirSetLocal { Name = "sequence", Value = Text("stored") };
            var boxed = Local("boxed");
            var restore = new BirCast { Type = T(CharSeq), E = boxed, FunctionValueRestoration = true };
            var arrayCast = new BirCast { Type = new TypeNode.Array(T(CharSeq)), E = Local("boxed") };
            var parameters = new[] {
                new BirParameter { Name = "sequence", Type = T(CharSeq) }, new BirParameter { Name = "custom", Type = T(CharSeq) },
                new BirParameter { Name = "boxed", Type = T("object") },
            };
            var method = new BirFunctionDeclaration { Name = "owner", Params = parameters, Ret = T("object"), Body = new IBirStatement[] {
                first, Statement(after), Statement(before), late, Statement(block), Statement(nullable), Statement(join),
                Statement(physical), Statement(source), store, Statement(restore), Statement(arrayCast),
            } };
            var root = new BirFileDeclaration { Methods = new[] { method }, Types = Array.Empty<IBirTypeDeclaration>() };
            var result = Apply(root, null, null);
            Require(ReferenceEquals(root.Methods.Value[0], method) && ReferenceEquals(method.Params.Value, parameters)
                && ReferenceEquals(method.Body.Value[0], first) && ReferenceEquals(first.Init.Value, original), "declaration/parameter/local identity changed");
            Require(Adapted(after.Args.Value[0]) && before.Args.Value[0] is BirLocal,
                "statement scope must see preceding locals only");
            Require(Adapted(resultCall.Args.Value[0]) && ReferenceEquals(block.Result.Value, resultCall), "valueBlock result scope/identity");
            Require(nullable.Args.Value[0] is BirValueBlock { Stmts.Value: [BirVariable temp], Result.Value: BirConditional conditional }
                && ReferenceEquals(temp.Init.Value, nullableValue)
                && conditional.Then.Value is BirConstant { Value.Value: BirLiteral.NullLiteral }
                && Adapted(conditional.Else.Value), "nullable effectful input must be evaluated once and preserve null");
            Require(Adapted(join.Then.Value) && ReferenceEquals(join.Else.Value, custom), "conditional branches must preserve custom values");
            Require(ReferenceEquals(physical.Args.Value[0], physicalValue) && !source.Sig.IsPresent
                && ReferenceEquals(source.ShapeTypes.Value, shape) && source.DeclarationId.Value == "selected"
                && source.Args.Value.Length == 2 && Adapted(source.Args.Value[0]) && ReferenceEquals(source.Args.Value[1], witness),
                "physical signature precedence/source shape/witness/identity changed");
            Require(SourceSpecializeCallType(new TypeNode.Tv("type", 0), new BirCallStatic {
                    OwnerType = new TypeNode.Fqn("Declared", new[] { T(CharSeq) }),
                    Owner = new TypeNode.Fqn("Physical", new[] { T("kotlin.Int") }),
                }) is TypeNode.Fqn { Name: CharSeq }, "call ownerType must precede owner during specialization");
            Require(Adapted(store.Value.Value), "local reassignment boundary");
            var restored = ((BirExpressionStatement)method.Body.Value[10]).Expr.Value;
            Require(restored is BirValueBlock { Stmts.Value: [BirVariable savedValue], Result.Value: BirConditional }
                && ReferenceEquals(savedValue.Init.Value, boxed) && !restore.FunctionValueRestoration.IsPresent
                && ReferenceEquals(((BirExpressionStatement)method.Body.Value[11]).Expr.Value, arrayCast), "opaque restoration must be scalar/once only");
            Require(result.AppendedTypes is [var adapter] && ReferenceEquals(root.Types.Value[0], adapter)
                && adapter.Name.Value == Adapter && adapter.Base.IsNull
                && adapter.Methods.Value.Select(value => value.Name.Value).SequenceEqual(new[] { "get", "subSequence", "value", "length", "ToString" })
                && adapter.Ctors.Value[0].BaseArgs.IsNull && adapter.Ctors.Value[0].ThisArgs.IsNull
                && adapter.Methods.Value[0].Mods.Value.Operator.Value && !adapter.Methods.Value[2].Attrs.IsPresent,
                "single adapter declaration shape/order/presence");
            Require(result.DelegateAdapters.Count == 0 && Apply(new BirFileDeclaration { Methods = Array.Empty<BirFunctionDeclaration>() }, null, null).AppendedTypes.Count == 0,
                "adapter emission must remain process-wide, not per-file");

            var parameterType = new TypeNode.Tv("method", 2);
            var sourceType = new TypeNode.Fn(false, T("kotlin.String"), new TypeNode[] { parameterType });
            var targetType = new TypeNode.Fn(false, T(CharSeq), new TypeNode[] { parameterType });
            var callback = Local("callback");
            var consume = Call(callback, targetType);
            var callbackMethod = new BirFunctionDeclaration { Name = "callbackOwner", Params = new[] { new BirParameter { Name = "callback", Type = sourceType } },
                TypeParams = new[] { new BirTypeParameter { Name = "T0" }, new BirTypeParameter { Name = "T1" }, new BirTypeParameter { Name = "T2" } },
                Ret = T("object"), Pos = new BirDeclarationPosition { F = "scope.kt", L = 12 }, Body = new IBirStatement[] { Statement(consume) } };
            var callbackFile = new BirFileDeclaration { Methods = new[] { callbackMethod }, Types = Array.Empty<IBirTypeDeclaration>() };
            var callbackResult = Apply(callbackFile, null, null);
            Require(callbackResult.DelegateAdapters is [var occurrence] && ReferenceEquals(occurrence.Slot.Value, consume.Args.Value[0])
                && occurrence.Context.Owner == "callbackOwner" && occurrence.Context.Path == "scope.kt" && occurrence.Context.Line == 12
                && occurrence.Slot.Value is BirNewClosure { Captures.Value: [var captured], SynthClass.Value: var ingredients, TypeArgs.Value: [TypeNode.Tv { Scope: "method", I: 2 }] }
                && ReferenceEquals(captured, callback) && ingredients.TypeParams.Value is [{ Name.Value: "T0" }]
                && ingredients.Body.Value is [BirReturn { Value.Value: BirNew { Args.Value: [BirDelegateInvoke] } }],
                "callback actual slot/source capture/context/caller frame");
            var materialized = ClosureSynthesis.ApplyMaterialized(callbackFile, callbackResult.DelegateAdapters, null);
            Require(materialized.AppendedTypes.Count == 1 && callbackFile.Types.Value.Length == 1
                && ReferenceEquals(callbackFile.Types.Value[0], materialized.AppendedTypes[0])
                && consume.Args.Value[0] is BirNewClosure { SynthClass.IsPresent: false }, "existing typed closure capability did not consume the published occurrence");

            var lifted = new BirFunctionDeclaration { Name = "lifted", Params = Array.Empty<BirParameter>(), Ret = T("System.String"),
                Body = new IBirStatement[] { new BirReturn { Value = Text("return") } } };
            var literal = new BirNewDelegate { Method = "lifted", FuncType = new TypeNode.Fn(false, T("kotlin.String"), Array.Empty<TypeNode>()) };
            var liftedFile = new BirFileDeclaration { Methods = new[] { lifted, new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { Statement(literal) } } }, Types = Array.Empty<IBirTypeDeclaration>() };
            Apply(liftedFile, null, new(new HashSet<string>(StringComparer.Ordinal) { "lifted" }, new HashSet<string>(StringComparer.Ordinal)));
            Require(lifted.Ret.Value is TypeNode.Fqn { Name: CharSeq }
                && lifted.Body.Value[0] is BirReturn { Value.Value: BirNew }
                && literal.FuncType.Value is TypeNode.Fn { Ret: TypeNode.Fqn { Name: CharSeq } }, "lifted return/body/delegate signature synchronization");

            var outer = new BirClassDefinition { Kind = "class", Name = "Derived", Base = new TypeNode.Fqn("Base", new[] { T(CharSeq) }),
                Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), DelegationSig = new TypeNode[] { new TypeNode.Tv("type", 0) },
                    BaseArgs = new IBirExpression[] { Text("base") } } } };
            Apply(new BirFileDeclaration { Types = new IBirTypeDeclaration[] { outer } }, null, null);
            Require(Adapted(outer.Ctors.Value[0].BaseArgs.Value[0]), "constructed base delegation must specialize in the actual owner frame");
            var asserted = new BirValueBlock { Result = new BirConditional {
                Cond = new BirUnaryOperation { Op = "!", E = new BirObjectEquals { Lhs = Local("x"), Rhs = new BirConstant { Value = new BirLiteral.NullLiteral() } } },
                Then = Local("x"), Else = new BirThrowExpression { Value = new BirNew { Type = T("System.InvalidOperationException"), Args = Array.Empty<IBirExpression>() } },
            } };
            Require(SourceIsNotNullAssertion(asserted), "current null-literal non-null assertion shape");
            ((BirObjectEquals)((BirUnaryOperation)((BirConditional)asserted.Result.Value).Cond.Value).E.Value).Rhs = Text("not-null");
            Require(!SourceIsNotNullAssertion(asserted), "non-null comparison is not a non-null assertion");
            var opaqueBody = new IBirStatement[] { Statement(Call(Text("opaque"), T(CharSeq))) };
            BirGraphValue payload = new BirInlinePayload { Body = opaqueBody };
            WalkSourceBridge(new BirGraphSlot(() => payload, value => payload = value),
                new SourceBridgeEnv(new("<file>", null, null)), new());
            Require(ReferenceEquals(((BirInlinePayload)payload).Body.Value, opaqueBody)
                && ((BirCallStatic)((BirExpressionStatement)opaqueBody[0]).Expr.Value).Args.Value[0] is BirConstant,
                "executable carrier must remain opaque");
            Console.WriteLine("[selftest] Owning String/CharSequence bridge: OK");
        }
    }
}
