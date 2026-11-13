using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class TypeOwnershipLowering
{
    internal static void SelfTestSourcePreparation()
    {
        var outer = Class("Outer", "O");
        var lifted = Class("Lifted", "Own", "O");
        lifted.SemanticOwner = "Outer"; lifted.OuterTypeParamCount = 1; lifted.OuterTypeParamOffset = 1;
        var child = Class("Lifted.Child", "Own", "O", "Child");
        child.SemanticOwner = "Lifted"; child.OuterTypeParamCount = 2;
        var field = new BirFieldDeclaration { Name = "own", Type = Tv(0), KotlinType = Tv(0) };
        lifted.Fields = new[] { field };
        var descriptor = new BirFieldGet { Name = "value", MemberType = Tv(0),
            MemberOwnerTypeParams = new[] { new BirTypeParameter { Name = "R", Constraints = new TypeNode[] { Tv(0) } } } };
        var call = new BirCallStatic { Method = "read", Sig = new TypeNode[] { Tv(0) } };
        var construction = new BirNew { Type = Named("Target"), ArgTypes = new TypeNode[] { Tv(0) },
            MemberSignature = new TypeNode[] { Tv(0) } };
        lifted.Methods = new[] { new BirFunctionDeclaration { Name = "read", Body = new IBirStatement[] { Expr(descriptor), Expr(call), Expr(construction) } } };
        var application = new BirFieldDeclaration { Name = "application", Type = new TypeNode.Fqn("Lifted", new TypeNode[] { Named("OwnArgument"), Named("OuterArgument") }) };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { outer, lifted, child }, Fields = new[] { application } };
        PrepareOwnershipFacts(new[] { root });
        if (!lifted.TypeParams.Value.Select(parameter => parameter.Name.Value).SequenceEqual(new[] { "O", "Own" })
            || !child.TypeParams.Value.Select(parameter => parameter.Name.Value).SequenceEqual(new[] { "O", "Own", "Child" })
            || field.Type.Value != Tv(1) || field.KotlinType.Value != Tv(0) || call.Sig.Value.Single() != Tv(0)
            || construction.ArgTypes.Value[0] != Tv(1) || construction.MemberSignature.Value[0] != Tv(1)
            || descriptor.MemberType.Value != Tv(1) || descriptor.MemberOwnerTypeParams.Value[0].Constraints.Value[0] != Tv(1)
            || application.Type.Value != new TypeNode.Fqn("Lifted", new TypeNode[] { Named("OuterArgument"), Named("OwnArgument") })
            || lifted.OuterTypeParamOffset.IsPresent || !ReferenceEquals(root.Types.Value[1], lifted))
            throw new InvalidOperationException("Ownership prefix preparation changed descriptor boundaries, source facts, or exact declaration identity");

        var ownerFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        var liveOwner = Class("LiveOwner", "O", "PO");
        liveOwner.KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame);
        var denseRead = new BirFieldGet { Name = "value", Sty = Tv(0), MemberType = Tv(0) };
        var denseCall = new BirCallStatic { Method = "read", TypeArgs = new TypeNode[] { Tv(0) }, Sig = new TypeNode[] { Tv(0) } };
        var nested = new BirClosureDefinition { Name = "Nested", TypeParams = new[] { Parameter("N") },
            Ret = Tv(0), _syntheticFrameBound = true };
        var nestedCall = new BirNewClosure { SynthClass = nested, TypeArgs = new TypeNode[] { Tv(0) } };
        var dense = new BirClosureDefinition { Name = "Dense", SemanticOwner = "LiveOwner", _syntheticFrameBound = true,
            TypeParams = new[] { Parameter("M") }, Ret = Tv(0),
            Body = new IBirStatement[] { Expr(denseRead), Expr(denseCall), Expr(nestedCall) } };
        var createDense = new BirNewClosure { SynthClass = dense, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) } };
        var samRead = new BirFieldGet { Name = "captured", Sty = Tv(0), MemberType = Tv(0) };
        var sam = new BirClassDefinition { Kind = "class", Name = "Sam", SemanticOwner = "LiveOwner", _syntheticFrameBound = true,
            TypeParams = new[] { Parameter("M") }, Methods = new[] { new BirFunctionDeclaration {
                Name = "invoke", Ret = Tv(0), Body = new IBirStatement[] { Expr(samRead) } } } };
        var createSam = new BirNewSam { SynthClass = sam, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) } };
        var live = new BirFileDeclaration { FileClass = "LiveFile", Types = new IBirTypeDeclaration[] { liveOwner },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { Expr(createDense), Expr(createSam) } } } };
        var consumerMethod = new BirFunctionDeclaration { Name = "consume", Body = Array.Empty<IBirStatement>() };
        var consumer = new BirFileDeclaration { FileClass = "Consumer", Methods = new[] { consumerMethod } };
        var facts = PrepareOwnershipFacts(new[] { live, consumer });
        if (dense.Ret.Value != Tv(2) || denseRead.Sty.Value != Tv(2) || denseRead.MemberType.Value != Tv(0)
            || nested.Ret.Value != Tv(0) || nestedCall.TypeArgs.Value[0] != Tv(2))
            throw new InvalidOperationException("Synthetic owner remapping entered a foreign descriptor or nested declaration frame");
        if (denseCall.TypeArgs.Value[0] != Tv(2) || denseCall.Sig.Value[0] != Tv(0)
            || sam.Methods.Value[0].Ret.Value != Tv(2) || samRead.Sty.Value != Tv(2) || samRead.MemberType.Value != Tv(0)
            || sam.OuterTypeParamCount.Value != 2 || createSam.TypeArgs.Value.Length != 3)
            throw new InvalidOperationException("Synthetic owner preparation lost SAM coverage or entered a foreign signature vector");

        // All inline files finish before this continuation. The existing module
        // index must read the current actual owner, even from another file; it
        // must not freeze constraints or rerun initial prefix normalization.
        liveOwner.TypeParams.Value[0].Constraints = new TypeNode[] { Named("CurrentBound") };
        liveOwner.KotlinSupertypesPre = new SourceTypeFacts(new Dictionary<int, TypeNode[]> {
            [0] = new TypeNode[] { Named("CurrentBound") },
        }).MergeNullableFrame(ownerFrame);
        var lateOwner = new BirClosureDefinition { Name = "Late", SemanticOwner = "LiveOwner", TypeParams = Array.Empty<BirTypeParameter>() };
        var late = new BirNewClosure { SynthClass = lateOwner, TypeArgs = Array.Empty<TypeNode>() };
        consumerMethod.Body = new IBirStatement[] { Expr(late) };
        PrepareSplicedOwnershipFacts(consumer, facts);
        if (lateOwner.TypeParams.Value[0].Constraints.Value.Single() != Named("CurrentBound")
            || lateOwner.KotlinSupertypesPre.Value.Bounds[0].Single() != Named("CurrentBound")
            || !ReferenceEquals(facts.SourceTypes["LiveOwner"], liveOwner)
            || !ReferenceEquals(live.Types.Value[0], liveOwner) || !ReferenceEquals(consumer.Methods.Value[0], consumerMethod)
            || !ReferenceEquals(late.SynthClass.Value, lateOwner))
            throw new InvalidOperationException("Post-inline ownership read stale source facts or replaced a continuing declaration");
        facts.ReleaseSourceDeclarations();
        var released = false;
        try { PrepareSplicedOwnershipFacts(consumer, facts); }
        catch (InvalidOperationException) { released = true; }
        if (!released) throw new InvalidOperationException("Ownership source declarations remained live after their continuation");
        Console.WriteLine("[source ownership] self-test OK (prefix composition, descriptor roles, frozen facts, dense nested frames, live post-inline owners and release)");

        static TypeNode Tv(int index) => new TypeNode.Tv("type", index);
        static TypeNode Named(string name) => new TypeNode.Fqn(name);
        static BirTypeParameter Parameter(string name) => new() { Name = name };
        static BirClassDefinition Class(string name, params string[] parameters) => new() {
            Kind = "class", Name = name, TypeParams = parameters.Select(Parameter).ToArray(),
        };
        static BirExpressionStatement Expr(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
    }
}
