using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void AlignmentSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning inner-base alignment: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var baseType = new TypeNode.Fqn("Base");
        var other = new TypeNode.Fqn("Unrelated");
        var nullableBase = new TypeNode.Nullable(baseType);
        var variable = new TypeNode.Tv("type", 0);
        var innerType = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Tv("method", 0) });
        BirLocal Inner() => new() { Name = "inner" };
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        BirCallStatic Call(TypeNode[] signature) => new() { Owner = new TypeNode.Fqn("Functions"), Method = "consume",
            Sig = signature, Args = new IBirExpression[] { Inner() }, Ret = unit };
        var sig = Call(new TypeNode[] { baseType });
        sig.ArgTypes = new TypeNode[] { other };
        sig.MemberSignature = new TypeNode[] { other };
        var argTypes = Call(null);
        argTypes.Sig = OptionalField<TypeNode[]>.Null;
        argTypes.ArgTypes = new TypeNode[] { baseType };
        argTypes.MemberSignature = new TypeNode[] { other };
        var member = Call(null);
        member.Sig = default;
        member.ArgTypes = OptionalField<TypeNode[]>.Null;
        member.MemberSignature = new TypeNode[] { baseType };
        var empty = Call(Array.Empty<TypeNode>());
        empty.ArgTypes = new TypeNode[] { baseType };
        var wrongArity = Call(new TypeNode[] { baseType, baseType });
        var unrelated = Call(new TypeNode[] { other });
        var genericTarget = Call(new TypeNode[] { new TypeNode.Fqn("Middle", new TypeNode[] { variable }) });
        var construction = new BirNew { Type = new TypeNode.Fqn("Target"), ArgTypes = new TypeNode[] { baseType },
            MemberSignature = new TypeNode[] { other }, Args = new IBirExpression[] { Inner() } };
        var physicalConstruction = new BirNewClr { Type = new TypeNode.Fqn("Target"),
            MemberSignature = new TypeNode[] { baseType }, Args = new IBirExpression[] { Inner() } };
        var instance = new BirCallInstance { OwnerType = baseType, Method = "read", Recv = Inner(),
            Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = unit };
        var before = new BirSetLocal { Name = "slot", Value = Inner() };
        var declaration = new BirVariable { Name = "slot", Type = baseType, Init = Inner() };
        var after = new BirSetLocal { Name = "slot", Value = Inner() };
        var scopedCall = Call(new TypeNode[] { baseType });
        var nested = new BirBlock { Body = new IBirStatement[] {
            new BirVariable { Name = "inner", Type = other }, Statement(scopedCall) } };
        var afterNested = Call(new TypeNode[] { baseType });
        var sideEffect = new BirCallStatic { Owner = new TypeNode.Fqn("Functions"), Method = "once",
            Args = Array.Empty<IBirExpression>(), Ret = innerType };
        var returned = new BirReturn { Value = sideEffect };
        var returnedExpression = new BirReturnExpression { Value = Inner() };
        var localReturn = new BirReturn { Value = Inner() };
        var local = new BirFunctionDeclaration { Name = "local", Ret = innerType,
            Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { localReturn } };
        var donorReturn = new BirReturn { Value = Inner() };
        var donor = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(), SuspendRet = baseType,
            Body = new IBirStatement[] { donorReturn } };
        var sequenceResult = new BirReturnExpression { Value = new BirLocal { Name = "blockLocal" } };
        var valueBlock = new BirValueBlock { Stmts = new IBirStatement[] {
            new BirVariable { Name = "blockLocal", Type = innerType } }, Result = sequenceResult };
        var noBodyReturn = new BirReturn { Value = Inner() };
        var constructorReturn = new BirReturn { Value = Inner() };
        var constructor = new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { constructorReturn } };
        var metadata = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, new BirInlinePayload {
            V = 1, Fqn = "Functions.inline", Owner = "Functions", Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "inner", Type = innerType } }, Ret = baseType,
            Body = new IBirStatement[] { new BirReturn { Value = Inner() } } });
        var metadataSnapshot = metadata.ToArray();
        var method = new BirFunctionDeclaration { Name = "probe", DeclarationId = "exact-probe", Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "inner", Type = innerType }, new BirParameter { Name = "slot", Type = other } },
            Ret = nullableBase, InlineBir = metadata, Body = new IBirStatement[] {
                before, declaration, after, Statement(sig), Statement(argTypes), Statement(member), Statement(empty), Statement(wrongArity),
                Statement(unrelated), Statement(genericTarget), Statement(construction), Statement(physicalConstruction), Statement(instance),
                nested, Statement(afterNested), new BirLocalFunction { Id = "local", Decl = local }, Statement(donor),
                Statement(valueBlock), Statement(returnedExpression), returned } };
        var bodyOnly = new BirFunctionDeclaration { Name = "bodyOnly", Ret = baseType,
            Body = new IBirStatement[] { noBodyReturn } };
        var root = new BirFileDeclaration { FileClass = "Functions", Methods = new[] { method, bodyOnly }, Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Base", Kind = "class" },
            new BirClassDefinition { Name = "Unrelated", Kind = "class" },
            new BirClassDefinition { Name = "Outer", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            new BirClassDefinition { Name = "Middle", Kind = "class", Base = baseType, TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            new BirClassDefinition { Name = "Outer.Inner", Kind = "class", SemanticOwner = "Outer",
                Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Base = new TypeNode.Fqn("Middle", new TypeNode[] { variable }),
                Ctors = new[] { constructor } },
        } };
        var methods = root.Methods.Value;
        var types = root.Types.Value;
        var parameters = method.Params.Value;
        var body = method.Body.Value;
        var values = GenericRepresentationPolicy.CreateSource(new[] { root }, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(new[] { root }, values, out var preparation);
        var demand = PrepareCarrierDemand(preparation, PrepareNativeProofs(bindings, refs, values), refs, values);
        AlignCapturedInnerBaseValuesSource(root, demand, refs);
        Require(sig.Args.Value[0] is BirCast && argTypes.Args.Value[0] is BirCast && member.Args.Value[0] is BirCast
            && empty.Args.Value[0] is BirLocal && wrongArity.Args.Value[0] is BirLocal
            && unrelated.Args.Value[0] is BirLocal && genericTarget.Args.Value[0] is BirLocal
            && construction.Args.Value[0] is BirCast && physicalConstruction.Args.Value[0] is BirCast && instance.Recv.Value is BirCast,
            "signature priority/presence/arity, construction, receiver or declaration-proven base selection changed");
        Require(before.Value.Value is BirLocal && declaration.Init.Value is BirCast && after.Value.Value is BirCast
            && scopedCall.Args.Value[0] is BirLocal && afterNested.Args.Value[0] is BirCast
            && localReturn.Value.Value is BirLocal && donorReturn.Value.Value is BirLocal
            && sequenceResult.Value.Value is BirLocal && noBodyReturn.Value.Value is BirLocal && constructorReturn.Value.Value is BirLocal
            && returnedExpression.Value.Value is BirCast,
            "preceding locals, block isolation, nested callable returns or params+body presence changed");
        Require(returned.Value.Value is BirCast { Type.HasValue: true, Sty.HasValue: true, _exactBridgeCast.Value: true } cast
            && cast.Type.Value == nullableBase && cast.Sty.Value == nullableBase
            && !ReferenceEquals(cast.Type.Value, nullableBase) && !ReferenceEquals(cast.Type.Value, cast.Sty.Value)
            && cast.E.Value is BirCallStatic { Method.Value: "once" } && ReferenceEquals(cast.E.Value, sideEffect),
            "bridge cast lost exact target/nullability, its physical marker or the owning operand");
        var calls = 0;
        root.Visit(node => { if (node is BirCallStatic { Method.Value: "once" }) calls++; });
        Require(calls == 1 && ReferenceEquals(methods, root.Methods.Value) && ReferenceEquals(types, root.Types.Value)
            && ReferenceEquals(parameters, method.Params.Value) && ReferenceEquals(body, method.Body.Value)
            && ReferenceEquals(method.InlineBir.Value, metadata) && metadata.AsSpan().SequenceEqual(metadataSnapshot)
            && method.DeclarationId.Value == "exact-probe" && method.Static.Value && ReferenceEquals(method.Ret.Value, nullableBase),
            "alignment changed evaluation multiplicity, declaration identity, frame facts or opaque executable metadata");
        Console.WriteLine("[owning inner-base alignment] self-test OK (scope, signatures, source frames, exact base proof, ownership and evaluation)");
        AlignmentDeclarationHandoffSelfTest(refs);
    }

    static void AlignmentDeclarationHandoffSelfTest(ReferenceMetadataIndex refs)
    {
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var baseType = new TypeNode.Fqn("Base");
        var innerType = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        var donor = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(),
            Body = Array.Empty<IBirStatement>(), SuspendRet = unit };
        var construction = new BirNew { Type = innerType, Args = new IBirExpression[] { donor } };
        var returned = new BirReturn { Value = construction };
        var method = new BirFunctionDeclaration { Name = "make", Ret = baseType,
            Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { returned } };
        var root = new BirFileDeclaration { FileClass = "Handoff", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Name = "Base", Kind = "class" },
                new BirClassDefinition { Name = "Outer.Inner", Kind = "class", Vis = "public",
                    Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1, Base = baseType,
                    TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
            } };
        var roots = new[] { root };
        var values = GenericRepresentationPolicy.CreateSource(roots, refs.Aliases, refs);
        var bindings = PrepareCurrentDeclarations(roots, values, out var preparation);
        var proofs = PrepareNativeProofs(bindings, refs, values);
        var donorBinding = bindings.GetSource(donor);
        var surface = PrepareCarrierSurface(preparation, proofs, refs, values);
        AlignCapturedInnerBaseValuesSource(root, surface.Demand, refs);
        ProjectParameterDeclarations(roots, CarrierProjectionContext.ForSource(surface.Demand, refs));
        if (returned.Value.Value is not BirCast cast || !ReferenceEquals(cast.E.Value, construction)
            || !ReferenceEquals(construction.Args.Value[0], donor))
            throw new InvalidOperationException("Inner-base wrapping replaced a captured operand or callable");
        System.Text.Json.Nodes.JsonObject actualDonor = null;
        var occurrences = 0;
        root.ToDom((source, physical) => {
            bindings.ObserveProjection(source, physical);
            if (ReferenceEquals(source, donor)) { actualDonor = physical; occurrences++; }
        });
        bindings.BindProjected();
        if (occurrences != 1 || actualDonor == null || !ReferenceEquals(donorBinding.Physical, actualDonor))
            throw new InvalidOperationException("Aligned nested callable lost its unique observed continuation");
        Console.WriteLine("[owning alignment handoff] self-test OK (captured operand, nested suspend donor and exact one-way binding)");
    }
}
