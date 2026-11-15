using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RefBodySquash
{
    internal static void SourceSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed reference body: " + message); }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var variable = new TypeNode.Tv("method", 0);
        var any = new TypeNode.Fqn("kotlin.Any");
        BirNewSuspendLambda Lambda(string id) => new() { DeclarationId = id, Arity = 0,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, TypeArgs = new TypeNode[] { variable },
            Params = Array.Empty<BirLambdaParameter>(), Captures = Array.Empty<BirCaptureDescriptor>(),
            CapValues = Array.Empty<IBirExpression>(), SuspendRet = any, Body = Array.Empty<IBirStatement>() };
        var donor = Lambda("body-donor"); var continuing = Lambda("prologue-donor");
        continuing.TypeArgs = new TypeNode[] { new TypeNode.Tv("type", 0) };
        var first = new BirExpressionStatement { Expr = donor };
        var second = new BirReturn { Value = new BirCast { Type = variable, ReifiedTypeOperand = false,
            E = new BirConstant { Type = new TypeNode.Nullable(any), Value = new BirLiteral.NullLiteral() } } };
        var defaultAttribute = DefaultBirCarrier.Create(0, new BirConstant {
            Type = new TypeNode.Nullable(any), Value = new BirLiteral.NullLiteral() });
        var payload = new BirInlinePayload { V = 1, Fqn = "ReferenceOwner.read", Owner = "ReferenceOwner", Recv = "none", Static = false,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } },
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Nullable(any) } }, Ret = variable,
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = variable,
                E = new BirLocal { Name = "value", Sty = new TypeNode.Nullable(any) } } } } };
        var payloadBytes = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, payload);
        var method = new BirFunctionDeclaration { Name = "read", DeclarationId = "reference-read", Static = false,
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Nullable(any), Attrs = new[] { defaultAttribute } } },
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, Ret = variable,
            InlineBir = payloadBytes, Body = new IBirStatement[] { first, second }, BodyTerminates = false };
        var empty = new BirFunctionDeclaration { Name = "empty", Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
        var absent = new BirFunctionDeclaration { Name = "absent" };
        var nullBody = new BirFunctionDeclaration { Name = "null", Body = OptionalField<IBirStatement[]>.Null };
        var abstractBody = new BirFunctionDeclaration { Name = "abstract", Abstract = true, Body = Array.Empty<IBirStatement>() };
        var constructorStatement = new BirReturn();
        var constructor = new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = any } },
            PreStmts = new IBirStatement[] { new BirExpressionStatement { Expr = continuing } },
            BaseArgs = new IBirExpression[] { new BirLocal { Name = "value" } },
            DelegationSig = new TypeNode[] { any }, Body = new IBirStatement[] { constructorStatement } };
        var thisConstructor = new BirConstructor { Params = Array.Empty<BirParameter>(), ThisArgs = Array.Empty<IBirExpression>(),
            Body = OptionalField<IBirStatement[]>.Null };
        var fieldInitializer = new BirConstant { Type = new TypeNode.Nullable(any), Value = new BirLiteral.NullLiteral() };
        var owner = new BirClassDefinition { Name = "ReferenceOwner", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { new BirFieldDeclaration { Name = "native", Type = new TypeNode.Tv("type", 0),
                Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } },
                new BirFieldDeclaration { Name = "initialized", Type = new TypeNode.Nullable(any), Init = fieldInitializer } },
            Methods = new[] { method, empty, absent, nullBody, abstractBody }, Ctors = new[] { constructor, thisConstructor } };
        var topStatement = new BirReturn();
        var top = new BirFunctionDeclaration { Name = "top", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { topStatement } };
        var file = new BirFileDeclaration { FileClass = "ReferenceFile", Methods = new[] { top }, Types = new IBirTypeDeclaration[] { owner } };
        var roots = new[] { file };
        var generic = GenericRepresentationPolicy.CreateSource(roots, new Dictionary<string, string>(), references);
        var values = ValueRepresentationLowering.CreateSource(roots, references, false,
            new ValueRepresentationSourcePolicy(roots, references, false), ValueRepresentationLowering.CaptureSource(roots), generic);
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
        Check(pending.SourceDeclarations.ByIdentity.ContainsKey("reference-read"), "fixture did not use the actual unchecked selector");
        pending.EraseOwnedReturn(pending.SourceDeclarations.ByIdentity["reference-read"]);
        generic.PrepareDomLocations(roots); generic.RefreshPhysicalNativeLocations(roots);
        var bindings = new CurrentDeclarationBindings(roots);
        var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(bindings, references, generic);
        var donorBinding = bindings.GetCapturedSource(donor);
        var continuingBinding = bindings.GetCapturedSource(continuing);
        var transitions = new SuspendColdLowering.SourcePipelineTransitions(roots, bindings, proofs, generic, null,
            values.RetireSourceDeclaration, Array.Empty<BirFunctionDeclaration>());
        var originalBody = method.Body.Value; var originalParameters = method.Params.Value; var originalFrame = method.TypeParams.Value;
        var pre = constructor.PreStmts.Value; var baseArgs = constructor.BaseArgs.Value; var ctorParams = constructor.Params.Value;
        var methods = owner.Methods.Value; var constructors = owner.Ctors.Value;
        var retired = new List<BirGraphValue>();
        Squash(file, actual => {
            Check(ReferenceEquals(actual, topStatement) && ReferenceEquals(top.Body.Value[0], topStatement)
                || (ReferenceEquals(actual, first) || ReferenceEquals(actual, second)) && ReferenceEquals(method.Body.Value, originalBody)
                || ReferenceEquals(actual, constructorStatement) && ReferenceEquals(constructor.Body.Value[0], constructorStatement),
                "retirement was reported after detach or for a non-body region");
            retired.Add(actual); transitions.RetireDiscardedSubtree(actual);
        });
        Check(retired.Count == 4 && ReferenceEquals(retired[0], topStatement) && ReferenceEquals(retired[1], first)
            && ReferenceEquals(retired[2], second) && ReferenceEquals(retired[3], constructorStatement), "body traversal/report order changed");
        Check(ReferenceEquals(owner.Methods.Value, methods) && ReferenceEquals(owner.Ctors.Value, constructors)
            && ReferenceEquals(method.Params.Value, originalParameters) && ReferenceEquals(method.TypeParams.Value, originalFrame)
            && ReferenceEquals(constructor.Params.Value, ctorParams) && ReferenceEquals(constructor.PreStmts.Value, pre)
            && ReferenceEquals(constructor.BaseArgs.Value, baseArgs) && ReferenceEquals(owner.Fields.Value[1].Init.Value, fieldInitializer)
            && method.BodyTerminates.Value == false && !thisConstructor.Body.HasValue && thisConstructor.Body.IsNull,
            "continuing declarations/prologue/frame/termination or field initializer changed");
        Check(method.Body.Value is [BirThrow { Value.Value: BirNewClr { Type.Value: TypeNode.Fqn { Name: "System.NotImplementedException" }, Args.Value.Length: 0, ArgTypes.Value.Length: 0 } }]
            && empty.Body.Value.Length == 1 && abstractBody.Body.Value.Length == 0 && !absent.Body.IsPresent && nullBody.Body.IsNull,
            "stub shape or abstract/empty/absent/null distinction changed");
        Check(ReferenceEquals(method.InlineBir.Value, payloadBytes) && payloadBytes.AsSpan().SequenceEqual(ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, payload))
            && ReferenceEquals(method.Params.Value[0].Attrs.Value[0], defaultAttribute), "opaque source payload was rewritten");
        var dom = file.ToDom(transitions.Projection.ObserveProjection);
        transitions.Projection.ProjectRetiredForests();
        var donorDom = transitions.Projection.Get(donor);
        Check(transitions.Projection.IsRetired(donor, donorDom) && !transitions.Projection.IsRetired(continuing, transitions.Projection.Get(continuing)),
            "retired body and continuing prologue overlapped");
        bindings.BindProjected();
        generic.BindDomDeclarations(new[] { dom }, declarations: transitions.Projection);
        values.BindDomDeclarations(new[] { dom }, transitions.Projection.RetiredMethods, transitions.Projection);
        var uncheckedIndex = UncheckedGenericCastReturnErasure.BindDomDeclarations(pending, new[] { dom },
            transitions.Projection.RetiredMethods, declarations: transitions.Projection);
        Check(ReferenceEquals(bindings.GetPhysical(donorDom), donorBinding)
            && ReferenceEquals(bindings.GetPhysical(transitions.Projection.Get(continuing)), continuingBinding)
            && ReferenceEquals(uncheckedIndex.ByIdentity["reference-read"].Method, transitions.Projection.Get(method))
            && generic.IsNativeField(transitions.Projection.Get(owner.Fields.Value[0])), "strict frontier lost the original callable/native/source authorities");
        transitions.Release();
        Console.WriteLine("[typed reference body] self-test OK (exact discarded forests, one frontier, strict buyers, prologue/payload/presence preservation)");
    }
}
