using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void NormalizationSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning carrier normalization: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var logical = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        var physical = new TypeNode.Fqn("Box$star");
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirLocal Local(string name, TypeNode type) => new() { Name = name, Sty = type };
        BirVariable Variable(string name, TypeNode type, IBirExpression init) => new() {
            Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(init),
        };
        BirFunctionDeclaration Method(string name, TypeNode result, params IBirStatement[] statements) => new() {
            Name = name, DeclarationId = name, Static = true, Params = Array.Empty<BirParameter>(), Ret = result, Body = statements,
        };
        var firstRead = Local("second", logical);
        var first = Variable("first", logical, firstRead);
        var projected = new BirCallStatic { Owner = new TypeNode.Fqn("External"), Method = "factory",
            Ret = physical, Args = Array.Empty<IBirExpression>() };
        var second = Variable("second", logical, projected);
        var operand = Local("first", logical);
        var cast = new BirCast { Type = logical, Sty = logical, E = OptionalField<IBirExpression>.Of(operand) };
        var nested = Method("nested", integer, new BirReturn { Value = OptionalField<IBirExpression>.Of(
            new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) }) });
        var provider = Method("provider", logical, first, second,
            new BirLocalFunction { Id = "nested", Decl = nested },
            new BirReturn { Value = OptionalField<IBirExpression>.Of(cast) });
        var call = new BirCallStatic { Owner = new TypeNode.Fqn("Normalization"), Method = "provider",
            DeclarationId = "provider", Ret = logical, Args = Array.Empty<IBirExpression>() };
        var consumer = Method("consumer", logical, new BirReturn { Value = OptionalField<IBirExpression>.Of(call) });
        var fallbackProvider = Method("fallbackProvider", logical,
            new BirReturn { Value = OptionalField<IBirExpression>.Of(Local("physical", physical)) });
        fallbackProvider.DeclarationId = default;
        var fallbackCall = new BirCallStatic { Owner = new TypeNode.Fqn("Normalization"),
            CalleeOwner = new TypeNode.Fqn("Normalization"), Method = "fallbackProvider",
            Ret = logical, DynRet = logical, Args = Array.Empty<IBirExpression>() };
        var fallbackConsumer = Method("fallbackConsumer", logical,
            new BirReturn { Value = OptionalField<IBirExpression>.Of(fallbackCall) });
        var factoryType = new TypeNode.Fqn("Factory");
        var instanceProvider = Method("instanceProvider", logical,
            new BirReturn { Value = OptionalField<IBirExpression>.Of(Local("physical", physical)) });
        instanceProvider.Static = false;
        var instanceCall = new BirCallInstance { DeclarationId = "instanceProvider", OwnerType = factoryType, Method = "instanceProvider",
            Ret = logical, DynRet = OptionalField<TypeNode>.Null, Args = Array.Empty<IBirExpression>(),
            Recv = OptionalField<IBirExpression>.Of(Local("factory", factoryType)) };
        var instanceConsumer = Method("instanceConsumer", logical,
            new BirReturn { Value = OptionalField<IBirExpression>.Of(instanceCall) });
        instanceConsumer.Params = new[] { new BirParameter { Name = "factory", Type = factoryType } };
        var constructorLocal = Variable("ctor", logical, Local("value", physical));
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { constructorLocal } };
        var owner = new BirClassDefinition { Name = "Box", Kind = "class", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>(),
            Ctors = new[] { constructor }, Interfaces = Array.Empty<TypeNode>() };
        var logicalArray = new TypeNode.Array(logical);
        var physicalArray = new TypeNode.Array(physical);
        var arrayFactory = new BirCallStatic { Owner = new TypeNode.Fqn("External"), Method = "arrayFactory",
            Ret = physicalArray, Args = Array.Empty<IBirExpression>() };
        var array = Variable("array", logicalArray, arrayFactory);
        var read = new BirArrayGet { Array = OptionalField<IBirExpression>.Of(Local("array", logicalArray)),
            Index = OptionalField<IBirExpression>.Of(new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) }),
            Elem = logical };
        var element = Variable("element", logical, read);
        var arrays = Method("arrays", logicalArray, array, element,
            new BirReturn { Value = OptionalField<IBirExpression>.Of(Local("array", logicalArray)) });
        var factory = new BirClassDefinition { Name = "Factory", Kind = "class", Vis = "public",
            Methods = new[] { instanceProvider }, Interfaces = Array.Empty<TypeNode>() };
        var root = new BirFileDeclaration { FileClass = "Normalization", Types = new IBirTypeDeclaration[] { owner, factory },
            Methods = new[] { consumer, provider, fallbackConsumer, fallbackProvider, instanceConsumer, arrays } };
        var context = SourceSynthesisTestContext(root, refs);
        context.Owners["Box"].Needed = true;
        var state = new SourceRewriteState(context, (_, _) => null);
        state.ProjectionFact(projected, logical);
        state.ArrayProjection(arrayFactory, logical);
        var originalMethods = root.Methods.Value;
        var originalBody = provider.Body.Value;
        NormalizeSourceFixedPoint(new[] { root }, state, Array.Empty<SharedSyntheticSynthesis.CellStorage>());
        Require(first.Type.Value == physical && second.Type.Value == physical && firstRead.Sty.Value == physical
            && cast.Type.Value == physical && cast.Sty.Value == physical && provider.Ret.Value == physical
            && ReferenceEquals(cast.E.Value, operand) && ReferenceEquals(provider.Body.Value, originalBody)
            && ReferenceEquals(root.Methods.Value, originalMethods),
            "reverse local propagation, post-order cast binding or original declaration/operand ownership changed");
        Require(nested.Ret.Value == integer && provider.RetKotlinType.HasValue
            && state.ProjectedType(firstRead)?.Name == "Box" && constructorLocal.Type.Value == physical,
            "local-function return filtering, semantic local projection, first-writer return fact or constructor locals changed");
        Require(call.Sty.Value == physical && call.Ret.Value == physical && consumer.Ret.Value == physical
            && fallbackCall.Sty.Value == physical && fallbackCall.Ret.Value == physical && fallbackCall.DynRet.Value == physical
            && instanceCall.Sty.Value == physical && instanceCall.Ret.Value == physical && instanceCall.DynRet.IsNull
            && instanceConsumer.Ret.Value == physical && fallbackConsumer.Ret.Value == physical,
            "declaration-ID/fallback return fixed point, mandatory sty or explicit-null dynRet preservation changed");
        Require(array.Type.Value == physicalArray && element.Type.Value == physical && arrays.Ret.Value == physicalArray
            && element.Init.Value is BirCast { E.Value: BirArrayGet current } && ReferenceEquals(current, read)
            && state.ProjectedRead(read),
            "array projection, live replacement ownership or array-return propagation changed");
        var stable = root.DeepClone();
        NormalizeSourceFixedPoint(new[] { root }, state, Array.Empty<SharedSyntheticSynthesis.CellStorage>());
        Require(root.StructurallyEquals(stable), "repeated normalization did not stabilize");

        // Constructor and suspend bodies participate in local normalization,
        // but do not acquire a function-declaration return inference contract.
        var suspendLocal = Variable("suspend", logical, Local("value", physical));
        var suspend = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(), Body = new IBirStatement[] { suspendLocal } };
        NormalizeInnerFactoryLocalsSource(suspend, state);
        Require(suspendLocal.Type.Value == physical, "suspend callable body lost local normalization");
        var late = Method("late", integer);
        NormalizeInnerFactoryLocalsSource(late, state);
        var lateLocal = Variable("late", logical, Local("value", physical));
        late.Body = new IBirStatement[] { lateLocal };
        NormalizeInnerFactoryLocalsSource(late, state);
        Require(lateLocal.Type.Value == physical && late.Ret.Value == integer,
            "an earlier empty-body visit cached a normalization decision or changed an unrelated return");
        var exact = new BirCast { Type = logical, _exactBridgeCast = true,
            E = OptionalField<IBirExpression>.Of(Local("value", physical)) };
        var exactMethod = Method("exact", logical, new BirReturn { Value = OptionalField<IBirExpression>.Of(exact) });
        NormalizeInnerFactoryLocalsSource(exactMethod, state);
        Require(ReferenceEquals(exact.Type.Value, logical) && exactMethod.Ret.Value == logical,
            "normalization erased an explicitly protected exact cast");
        NormalizationSourceWorklistSelfTest(refs);
        OwnerConstrainedMethodLowering.Reset(refs, _ => null);
        Console.WriteLine("[owning carrier normalization] self-test OK (live post-order/fixed points, ctor/suspend bodies, ID/fallback calls, presence and identities)");
    }

    static void NormalizationSourceWorklistSelfTest(ReferenceMetadataIndex refs)
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning normalization worklist: " + message); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var logical = new TypeNode.Fqn("WorklistBox", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        BirLocal Local(string name, TypeNode type) => new() { Name = name, Sty = type };
        BirVariable Variable(string name, TypeNode type, IBirExpression value) => new() {
            Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(value),
        };
        BirConstant One() => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(1) };
        BirFunctionDeclaration Method(TypeNode result, params IBirStatement[] body) => new() {
            Name = "worklist", Ret = result, Params = Array.Empty<BirParameter>(), Body = body,
        };
        var getter = new BirFunctionDeclaration { Name = "source_get", DeclarationId = "worklist-get", Vis = "private",
            PropertyName = "value", PropertyAccessor = "get", PropertyAssociation = "WorklistBox:field:value",
            Params = Array.Empty<BirParameter>(), Ret = integer,
            Body = new IBirStatement[] { new BirReturn { Value = One() } } };
        var setter = new BirFunctionDeclaration { Name = "source_set", DeclarationId = "worklist-set", Vis = "private",
            PropertyName = "value", PropertyAccessor = "set", PropertyAssociation = "WorklistBox:field:value",
            Params = new[] { new BirParameter { Name = "value", Type = integer } }, Ret = unit, Body = Array.Empty<IBirStatement>() };
        var definition = new BirClassDefinition { Name = "WorklistBox", Kind = "class", Vis = "public",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { getter, setter },
            Fields = new[] { new BirFieldDeclaration { Name = "value", Type = integer } } };
        var root = new BirFileDeclaration { FileClass = "Worklist", Types = new IBirTypeDeclaration[] { definition } };
        var context = SourceSynthesisTestContext(root, refs);
        var owner = context.Owners["WorklistBox"];
        SynthesizeSource(owner, context);
        var physical = new TypeNode.Fqn(owner.ErasedName);
        var state = new SourceRewriteState(context, (_, _) => null);

        // There are no inferred locals to trigger a second iteration. The
        // enclosing safe cast must observe the child cast after postorder binding.
        var inner = new BirCast { Type = logical, E = OptionalField<IBirExpression>.Of(Local("physical", physical)) };
        var outer = new BirInstanceOrNull { Type = logical, E = OptionalField<IBirExpression>.Of(inner) };
        var nested = Method(logical, new BirReturn { Value = OptionalField<IBirExpression>.Of(outer) });
        NormalizeInnerFactoryLocalsSource(nested, state);
        Require(inner.Type.Value == physical && outer.Type.Value == physical && nested.Ret.Value == physical
            && ReferenceEquals(outer.E.Value, inner), "nested type operation required an extra local-propagation iteration");

        foreach (var (declared, result) in new (TypeNode, TypeNode)[] {
            (new TypeNode.Nullable(logical), physical), (new TypeNode.Oblivious(logical), physical),
            (new TypeNode.Array(new TypeNode.Projection("out", logical)), new TypeNode.Array(physical)),
        })
        {
            var shaped = Method(declared, new BirReturn { Value = OptionalField<IBirExpression>.Of(Local("physical", result)) });
            NormalizeInnerFactoryLocalsSource(shaped, state);
            Require(shaped.Ret.Value == result && shaped.RetKotlinType.Value == declared,
                "return eligibility or the first-writer Kotlin result fact lost a projection/nullability shell");
        }

        var logicalArray = new TypeNode.Array(logical);
        var physicalArray = new TypeNode.Array(physical);
        var arrayProducer = new BirCallStatic { Owner = new TypeNode.Fqn("Worklist"), Method = "array",
            Args = Array.Empty<IBirExpression>(), Ret = physicalArray };
        state.ArrayProjection(arrayProducer, logical);
        var indexArgument = Local("b", logical);
        var index = new BirCallStatic { Owner = new TypeNode.Fqn("Worklist"), Method = "index", Ret = integer,
            Args = new IBirExpression[] { indexArgument } };
        var read = new BirArrayGet { Array = OptionalField<IBirExpression>.Of(Local("array", logicalArray)),
            Index = OptionalField<IBirExpression>.Of(index), Elem = logical };
        var use = new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(read) };
        var reverse = Method(integer, Variable("array", logicalArray, arrayProducer), use,
            Variable("b", logical, Local("a", logical)), Variable("a", logical, Local("physical", physical)));
        NormalizeInnerFactoryLocalsSource(reverse, state);
        Require(use.Expr.Value is BirCast replacement && ReferenceEquals(replacement.E.Value, read)
            && ReferenceEquals(read.Index.Value, index) && ReferenceEquals(index.Args.Value.Single(), indexArgument)
            && indexArgument.Sty.Value == physical && state.ProjectedRead(read),
            "next iteration did not recollect the live descendants moved below the array-read replacement");
        var occurrences = 0;
        reverse.Visit(node => { if (ReferenceEquals(node, indexArgument)) occurrences++; });
        Require(occurrences == 1, "recollection duplicated a continuing descendant");

        var slots = context.Declarations.Slots(owner).Values.ToDictionary(row => row.Original, row => row);
        var selectedGet = slots[context.Bindings.GetCapturedSource(getter)].Slot.Name.Value;
        var selectedSet = slots[context.Bindings.GetCapturedSource(setter)].Slot.Name.Value;
        foreach (var kind in new[] { "field", "lateinitGet", "setFieldExpr", "setField", "callInstance", "newBoundDelegate" })
        {
            var receiver = Local("receiver", logical);
            BirGraphValue access = kind switch {
                "field" => new BirFieldGet { OwnerType = logical, Name = "value", Recv = receiver, Ret = integer },
                "lateinitGet" => new BirLateinitGet { OwnerType = logical, Name = "value", Recv = receiver, Ret = integer },
                "setFieldExpr" => new BirFieldSetExpression { OwnerType = logical, Name = "value", Recv = receiver, Value = One(), Ret = unit },
                "setField" => new BirSetField { OwnerType = logical, Name = "value", Recv = receiver, Value = One() },
                "callInstance" => new BirCallInstance { OwnerType = logical, Method = "source_get", DeclarationId = "worklist-get",
                    Recv = receiver, Args = Array.Empty<IBirExpression>(), Ret = integer,
                    PropertyName = "value", PropertyAccessor = "get" },
                _ => new BirNewBoundDelegate { OwnerType = logical, Method = "source_get", DeclarationId = "worklist-get",
                    Recv = receiver, Sig = Array.Empty<TypeNode>(), CalleeRet = integer,
                    FuncType = new TypeNode.Fn(false, integer, Array.Empty<TypeNode>()) },
            };
            Require(ReferenceEquals(BindSourceNormalizedNode(access, state), access)
                && receiver.Sty.Value == logical, kind + " was rebound without its receiver's physical evidence");
            var statement = access is IBirStatement direct ? direct
                : new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)access) };
            var method = Method(integer, Variable("receiver", logical, Local("physicalReceiver", physical)), statement);
            NormalizeInnerFactoryLocalsSource(method, state);
            var selected = method.Body.Value[1] is BirExpressionStatement expression ? (BirGraphValue)expression.Expr.Value
                : (BirGraphValue)method.Body.Value[1];
            var expected = kind is "setField" or "setFieldExpr" ? selectedSet : selectedGet;
            if (selected is BirNewBoundDelegate bound)
                Require(bound.OwnerType.Value == physical && bound.CalleeOwner.Value == physical && bound.Method.Value == expected
                    && ReferenceEquals(bound.Recv.Value, receiver), "bound delegate target did not follow its normalized receiver");
            else Require(selected is BirCallInstance call && call.OwnerType.Value == physical && call.Method.Value == expected
                    && ReferenceEquals(call.Recv.Value, receiver), kind + " did not bind after receiver-local normalization");
        }
    }
}
