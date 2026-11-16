using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ContinuationErasure
{
    static void SourceProtocolSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning coroutine ABI: " + message); }
        var names = new Dictionary<string, string> { [Cont] = "probe.ContinuationPhysical", [ResultFqn] = "probe.ResultPhysical" };
        var integer = new TypeNode.Fqn("kotlin.Int"); var unit = new TypeNode.Fqn("kotlin.Unit");
        var any = new TypeNode.Fqn("kotlin.Any"); var original = new TypeNode.Fqn(Cont, new TypeNode[] { integer });
        var sourceFact = new TypeNode.Fqn(Cont, new TypeNode[] { new TypeNode.Tv("method", 0) });
        var defaultAttribute = DefaultBirCarrier.Create(0, new BirConstant { Type = new TypeNode.Nullable(original), Value = new BirLiteral.NullLiteral() });
        var defaultBytes = ((BirOpaqueBytes)defaultAttribute.Args.Value[2]).Bytes.Value;
        var encoded = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, new BirInlinePayload {
            V = 1, Fqn = "Probe.inline", Owner = "Probe", Recv = "none", Static = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Nullable(original),
            Body = new IBirStatement[] { new BirReturn { Value = new BirConstant {
                Type = new TypeNode.Nullable(original), Value = new BirLiteral.NullLiteral() } } },
        });
        BirCallStatic ResultCall(string method, bool resultReceiver = true) => new() {
            Owner = new TypeNode.Fqn("kotlin.ResultKt"), Method = method, TypeArgs = new TypeNode[] { integer },
            Args = new IBirExpression[] { new BirLocal { Name = resultReceiver ? "result" : "other" } }, Ret = unit, Sty = unit,
        };
        var promoted = ResultCall("getOrThrow"); var notResult = ResultCall("getOrThrow", false);
        var exception = ResultCall("exceptionOrNull"); exception.Ret = new TypeNode.Fqn("kotlin.Throwable");
        var nullStamp = ResultCall("getOrNull"); nullStamp.Sty = OptionalField<TypeNode>.Null;
        var absentStamp = ResultCall("getOrElse"); absentStamp.Sty = default;
        var lambdaParameter = new BirLambdaParameter { Name = "value", Type = original };
        var lambda = new BirNewSuspendLambda { Arity = 1, Params = new[] { lambdaParameter }, SuspendRet = unit,
            Captures = Array.Empty<BirCaptureDescriptor>(), CapValues = Array.Empty<IBirExpression>(), Body = Array.Empty<IBirStatement>() };
        var ordinary = new BirParameter { Name = "ordinary", Type = original, Attrs = new[] { defaultAttribute } };
        var nullable = new BirParameter { Name = "nullable", Type = original, NullableGeneric = sourceFact };
        var existing = new BirParameter { Name = "recorded", Type = original, KotlinType = sourceFact };
        var unchanged = new BirParameter { Name = "unchanged", Type = integer };
        var method = new BirFunctionDeclaration { Name = "resumeWith", Params = new[] { ordinary, nullable, existing, unchanged }, Ret = original,
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { original } } },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { original } }),
            SuspendResult = sourceFact, InlineBir = encoded, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = promoted }, new BirExpressionStatement { Expr = notResult },
                new BirExpressionStatement { Expr = exception }, new BirExpressionStatement { Expr = nullStamp },
                new BirExpressionStatement { Expr = absentStamp }, new BirExpressionStatement { Expr = lambda },
            } };
        var bounds = method.MethodTypeParameterBoundsPre.Value; var boundBytes = bounds.FrozenText;
        var retainedReturn = new BirFunctionDeclaration { Name = "retained", Params = Array.Empty<BirParameter>(), Ret = original, NullableGenericRet = sourceFact };
        var fieldCall = ResultCall("getOrThrow"); var normalFieldCall = ResultCall("getOrThrow");
        var field = new BirFieldDeclaration { Name = "resumeWith", Type = original, Init = fieldCall };
        var normalField = new BirFieldDeclaration { Name = "ordinaryField", Type = original, Init = normalFieldCall };
        var property = new BirPropertyDeclaration { Name = "property", Type = original };
        var factory = new BirCallStatic { Owner = new TypeNode.Fqn(ResultFqn), Method = "success", TypeArgs = new TypeNode[] { integer }, Args = Array.Empty<IBirExpression>() };
        var physicalFactory = new BirCallStatic { Owner = new TypeNode.Fqn(names[ResultFqn]), Method = "success", TypeArgs = new TypeNode[] { integer } };
        var staticContainer = new BirCallStatic { Owner = original, Method = "identity", TypeArgs = Array.Empty<TypeNode>() };
        var plain = new BirFunctionDeclaration { Name = "ordinary", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = factory }, new BirExpressionStatement { Expr = physicalFactory }, new BirExpressionStatement { Expr = staticContainer } } };
        var file = new BirFileDeclaration { Methods = new[] { method, retainedReturn, plain }, Fields = new[] { field, normalField }, Properties = new[] { property } };
        var statements = method.Body.Value; var parameters = method.Params.Value; var methods = file.Methods.Value;
        Apply(file, names);
        var erased = new TypeNode.Fqn(Cont, new TypeNode[] { any });
        EqualSourceType(ordinary.Type.Value, erased, "ordinary parameter ABI");
        EqualSourceType(lambdaParameter.Type.Value, erased, "lambda parameter ABI");
        EqualSourceType(ordinary.KotlinType.Value, original, "ordinary source surface");
        EqualSourceType(lambdaParameter.KotlinType.Value, original, "lambda source surface");
        EqualSourceType(field.KotlinType.Value, original, "field source surface");
        EqualSourceType(property.KotlinType.Value, original, "property source surface");
        EqualSourceType(method.RetKotlinType.Value, original, "return source surface");
        Require(!nullable.KotlinType.IsPresent && ReferenceEquals(nullable.NullableGeneric.Value, sourceFact)
            && ReferenceEquals(existing.KotlinType.Value, sourceFact) && !unchanged.KotlinType.IsPresent
            && !retainedReturn.RetKotlinType.IsPresent && ReferenceEquals(retainedReturn.NullableGenericRet.Value, sourceFact),
            "earlier source/nullable carrier precedence changed");
        EqualSourceType(promoted.TypeArgs.Value[0], any, "resume Result receiver application");
        EqualSourceType(promoted.Ret.Value, any, "resume Result receiver return");
        EqualSourceType(promoted.Sty.Value, any, "resume Result receiver stamp");
        EqualSourceType(notResult.TypeArgs.Value[0], integer, "ordinary receiver application");
        EqualSourceType(exception.TypeArgs.Value[0], any, "non-T-returning Result receiver application");
        EqualSourceType(exception.Ret.Value, new TypeNode.Fqn("kotlin.Throwable"), "non-T-returning Result receiver return");
        EqualSourceType(fieldCall.TypeArgs.Value[0], any, "named declaration initializer protocol");
        EqualSourceType(normalFieldCall.TypeArgs.Value[0], integer, "ordinary initializer application");
        EqualSourceType(factory.TypeArgs.Value[0], any, "Result factory application");
        EqualSourceType(physicalFactory.TypeArgs.Value[0], integer, "non-source factory identity");
        EqualSourceType(staticContainer.Owner.Value, original, "static container is not an application slot");
        Require(nullStamp.Sty.IsNull && !absentStamp.Sty.IsPresent
            && ReferenceEquals(method.Body.Value, statements) && ReferenceEquals(method.Params.Value, parameters) && ReferenceEquals(file.Methods.Value, methods)
            && ReferenceEquals(((BirExpressionStatement)method.Body.Value[0]).Expr.Value, promoted), "owning vectors, result presence or expression identity changed");
        Require(ReferenceEquals(method.SuspendResult.Value, sourceFact) && ReferenceEquals(method.MethodTypeParameterBoundsPre.Value, bounds)
            && bounds.FrozenText == boundBytes && ReferenceEquals(method.InlineBir.Value, encoded)
            && ReferenceEquals(((BirOpaqueBytes)ordinary.Attrs.Value[0].Args.Value[2]).Bytes.Value, defaultBytes), "frozen source or opaque executable bytes were rewritten");
        Console.WriteLine("[owning coroutine ABI] self-test OK (source precedence, lexical protocol, frozen facts, exact identities and result stamps)");
    }
}
