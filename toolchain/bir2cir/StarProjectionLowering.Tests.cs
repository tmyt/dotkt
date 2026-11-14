using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StarProjectionLowering
{
    internal static void SourceSelfTest()
    {
        var refs = ReferenceMetadataIndex.StarProjectionTestIndex();
        var stars = new TypeNode[] { new TypeNode.Star() };
        var list = new TypeNode.Fqn("kotlin.collections.List", stars);
        var iterable = new TypeNode.Fqn("kotlin.collections.Iterable", stars);
        var mutableIterable = new TypeNode.Fqn("kotlin.collections.MutableIterable", stars);
        var collection = new TypeNode.Fqn("kotlin.collections.Collection", stars);
        var count = new BirCallInstance { OwnerType = collection, Method = "size", Prop = "get", Args = Array.Empty<IBirExpression>(),
            Recv = new BirLocal { Name = "items" } };
        var test = new BirIsInstance { Type = list, NullMatches = true, E = new BirCallStatic { Method = "testEffect" } };
        var cast = new BirCast { Type = new TypeNode.Nullable(list), E = new BirCallStatic { Method = "castEffect" } };
        var concreteTarget = new TypeNode.Fqn("kotlin.collections.List", new[] { Any });
        var concrete = new BirInstanceOrNull { Type = concreteTarget, E = new BirLocal { Name = "value" } };
        var iterableCast = new BirCast { Type = iterable, E = new BirLocal { Name = "value" } };
        var rawIterator = new BirCallInstance { OwnerType = mutableIterable, Method = "iterator", Args = Array.Empty<IBirExpression>(),
            Recv = new BirCast { Type = mutableIterable, E = new BirCallStatic { Method = "iteratorEffect" } } };
        var body = new IBirStatement[] {
            new BirExpressionStatement { Expr = count }, new BirExpressionStatement { Expr = test },
            new BirExpressionStatement { Expr = cast }, new BirExpressionStatement { Expr = concrete },
            new BirExpressionStatement { Expr = iterableCast }, new BirExpressionStatement { Expr = rawIterator },
        };
        var method = new BirFunctionDeclaration { Name = "probe", Params = Array.Empty<BirParameter>(), Body = body };
        var methods = new[] { method };
        var file = new BirFileDeclaration { Methods = methods };
        Apply(file, refs);
        BirGraphValue At(int index) => (BirGraphValue)((BirExpressionStatement)body[index]).Expr.Value;
        var countCall = (BirCallStatic)At(0);
        var testCall = (BirCallStatic)At(1);
        var castCall = (BirCallStatic)At(2);
        var concreteResult = (BirInstanceOrNull)At(3);
        var iterator = (BirCallStatic)At(5);
        Check(ReferenceEquals(file.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && countCall.Method.Value == "projectedViewCountErased" && countCall.DeclarationId.Value == "selected-count"
            && countCall.TypeArgs.Value.Length == 0 && countCall.Args.Value.Length == 3
            && ((BirLiteral.IntegerLiteral)((BirConstant)countCall.Args.Value[2]).Value.Value).Value == 0,
            "static collection operations must preserve owner records and bind the exact helper with its family/preference operands");
        Check(testCall.Method.Value == "starProjectionKotlinNullableCollectionIsInstance"
            && testCall.Args.Value[0] is BirCallStatic { Method.Value: "testEffect" }
            && castCall.Method.Value == "starProjectionKotlinNullableCollectionCast" && castCall.DotktProjectedCollection.Value
            && concreteResult.Type.Value == concreteTarget && concreteResult.E.Value is BirCallStatic { Method.Value: "starProjectionKotlinCollectionSafeCast" }
            && ReferenceEquals(At(4), iterableCast) && iterableCast.DotktProjectedCollection.Value
            && iterableCast.Type.Value is TypeNode.Fqn { Name: "System.Collections.IEnumerable", Args: null },
            "nullable classifiers, concrete Any applications and raw Iterable facade provenance must remain distinct");
        Check(iterator.Method.Value == "clrMutableIteratorErased" && iterator.Args.Value[0] is BirCast rawCast
            && rawCast.Type.Value is TypeNode.Fqn { Name: "System.Collections.IEnumerable" }
            && rawCast.E.Value is BirCallStatic { Method.Value: "iteratorEffect" }
            && !rawCast.DotktProjectedCollection.IsPresent && UsedRuntimeFallback,
            "mutable Iterable dispatch must preserve its observable raw cast and original receiver effect");
        var nodes = new List<BirGraphValue>(); file.Visit(nodes.Add);
        Check(nodes.OfType<BirCallStatic>().Count(call => call.Method.OrDefault() == "castEffect") == 1
            && nodes.OfType<BirCallStatic>().Count(call => call.Method.OrDefault() == "iteratorEffect") == 1,
            "projection must not duplicate effectful operands");
        HelperFrameSelfTest(refs);
        Console.WriteLine("[typed star projection] self-test OK (physical helper binding, nullable classifiers, concrete views, raw protocol, copies)");
    }

    static void HelperFrameSelfTest(ReferenceMetadataIndex refs)
    {
        var callerFrame = new NullableRepresentationFrame(2, new[] { 1 });
        var method = new BirFunctionDeclaration { Name = "caller", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "First" }, new BirTypeParameter { Name = "Second" }, new BirTypeParameter { Name = "NullableSecond" } },
            Body = Array.Empty<IBirStatement>(), NullableRepresentationFrame = callerFrame };
        var context = new CollectionHelperBinding.CollectionSourceContext().Extend(method);
        var variable = callerFrame.Variable(new TypeNode.Tv("method", 1), NullableRepresentationFrame.Role.Ordinary);
        var receiver = new BirLocal { Name = "receiver" };
        var plan = MemberCallSubstitution.SelectStaticCollectionOperation("contains", null, 1, true,
            new TypeNode.Fqn("kotlin.collections.Collection", new TypeNode[] { variable }), variable, null);
        var original = new BirCallInstance { Recv = receiver, Args = new IBirExpression[] { new BirLocal { Name = "argument" } } };
        var source = CollectionHelperBinding.RenderCollectionOperation(plan, original, refs, MemberCallSubstitution.CollectionOperationPhase.Source);
        var physical = CollectionHelperBinding.RenderCollectionOperation(plan, original, refs, MemberCallSubstitution.CollectionOperationPhase.Physical);
        Check(ReferenceEquals(source.Args.Value[0], receiver) && ReferenceEquals(source.Args.Value[3], original.Args.Value[0])
            && !ReferenceEquals(physical.Args.Value[0], receiver) && !ReferenceEquals(physical.Args.Value[3], original.Args.Value[0])
            && source.TypeArgs.Value.Single() == variable && physical.TypeArgs.Value.Single() == variable,
            "the shared renderer must preserve source occurrences but snapshot physical authored operands");
        var physicalOperands = physical.Args.Value;
        MemberCallSubstitution.BindAuthoredHelper(physical, refs, context, _ => false);
        Check(physical.DeclarationId.Value == "selected-contains" && physical.TypeArgs.Value.Length == 2
            && physical.TypeArgs.Value[0] == variable && physical.TypeArgs.Value[1] == callerFrame.NullableVariable(new TypeNode.Tv("method", 1))
            && ReferenceEquals(physical.Args.Value, physicalOperands) && source.TypeArgs.Value.Length == 1,
            "physical helper closure must share the caller's authored nullable frame without mutating source binding or value operands");
        var ownerFrame = new NullableRepresentationFrame(1, new[] { 0 });
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "NullableT" } },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame) };
        var ownerContext = new CollectionHelperBinding.CollectionSourceContext().Extend(owner).Extend(
            new BirFunctionDeclaration { Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() });
        physical.TypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0) };
        MemberCallSubstitution.BindAuthoredHelper(physical, refs, ownerContext, _ => false);
        Check(ReferenceEquals(ownerContext.OwnerFrame, ownerFrame) && ownerContext.MethodFrame == null
            && physical.TypeArgs.Value[1] == ownerFrame.NullableVariable(new TypeNode.Tv("type", 0)),
            "method scope must keep its enclosing declaration frame while resetting the unrelated method frame");
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Typed star projection: " + message); }
}

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex StarProjectionTestIndex()
    {
        var index = Build(new[] { typeof(object).Assembly.Location });
        var any = new TypeNode.Fqn("kotlin.Any");
        var preference = new TypeNode.Nullable(new TypeNode.Fqn("object"));
        var integer = new TypeNode.Fqn("kotlin.Int");
        index._membersByOwner["DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"] = new List<MemberBinding> {
            new("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt", "projectedViewCountErased", 3, null, false, true,
                ParamTypeNodes: new TypeNode[] { any, preference, integer }, DeclarationId: "selected-count"),
        };
        var plan = MemberCallSubstitution.SelectStaticCollectionOperation("contains", null, 1, true,
            new TypeNode.Fqn("kotlin.collections.Collection"), new TypeNode.Fqn("object"), null);
        var signature = MemberCallSubstitution.CollectionOperationTypes(plan);
        index._membersByOwner["kotlin.collections.ClrCollectionDefaultsKt"] = new List<MemberBinding> {
            new("kotlin.collections.ClrCollectionDefaultsKt", "clrProjectedViewContains", signature.Length, null, false, true,
                ParamTypeNodes: signature, DeclarationId: "selected-contains", MethodArity: 2,
                NullableFrame: new NullableRepresentationFrame(1, new[] { 0 })),
        };
        return index;
    }
}
