using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Text.Json.Nodes;

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
        lifted.Methods = new[] { new BirFunctionDeclaration { Name = "read", Body = new IBirStatement[] { Expr(descriptor), Expr(call) } } };
        var application = new BirFieldDeclaration { Name = "application", Type = new TypeNode.Fqn("Lifted", new TypeNode[] { Named("OwnArgument"), Named("OuterArgument") }) };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { outer, lifted, child }, Fields = new[] { application } };
        PrepareOwnershipFacts(new[] { root });
        if (!lifted.TypeParams.Value.Select(parameter => parameter.Name.Value).SequenceEqual(new[] { "O", "Own" })
            || !child.TypeParams.Value.Select(parameter => parameter.Name.Value).SequenceEqual(new[] { "O", "Own", "Child" })
            || field.Type.Value != Tv(1) || field.KotlinType.Value != Tv(0) || call.Sig.Value.Single() != Tv(0)
            || descriptor.MemberType.Value != Tv(1) || descriptor.MemberOwnerTypeParams.Value[0].Constraints.Value[0] != Tv(1)
            || application.Type.Value != new TypeNode.Fqn("Lifted", new TypeNode[] { Named("OuterArgument"), Named("OwnArgument") })
            || lifted.OuterTypeParamOffset.IsPresent || !ReferenceEquals(root.Types.Value[1], lifted))
            throw new InvalidOperationException("Ownership prefix preparation changed descriptor boundaries, source facts, or exact declaration identity");

        var ownerFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        var liveOwner = Class("LiveOwner", "O", "PO");
        liveOwner.KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame);
        var denseRead = new BirFieldGet { Name = "value", Sty = Tv(0), MemberType = Tv(0) };
        var nested = new BirClosureDefinition { Name = "Nested", TypeParams = new[] { Parameter("N") },
            Ret = Tv(0), _syntheticFrameBound = true };
        var nestedCall = new BirNewClosure { SynthClass = nested, TypeArgs = new TypeNode[] { Tv(0) } };
        var dense = new BirClosureDefinition { Name = "Dense", SemanticOwner = "LiveOwner", _syntheticFrameBound = true,
            TypeParams = new[] { Parameter("M") }, Ret = Tv(0),
            Body = new IBirStatement[] { Expr(denseRead), Expr(nestedCall) } };
        var createDense = new BirNewClosure { SynthClass = dense, TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) } };
        var live = new BirFileDeclaration { FileClass = "LiveFile", Types = new IBirTypeDeclaration[] { liveOwner },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { Expr(createDense) } } } };
        var facts = PrepareOwnershipFacts(new[] { live });
        if (dense.Ret.Value != Tv(2) || denseRead.Sty.Value != Tv(2) || denseRead.MemberType.Value != Tv(0)
            || nested.Ret.Value != Tv(0) || nestedCall.TypeArgs.Value[0] != Tv(2))
            throw new InvalidOperationException("Synthetic owner remapping entered a foreign descriptor or nested declaration frame");

        // This terminal projection is the actual still-live splice boundary.
        // Mutate the continuing owner afterwards, proving no frozen typed copy
        // supplies its parameter constraints/source bounds to a late splice.
        var continuing = live.ToDom();
        facts.BindDomDeclarations(new JsonNode[] { continuing });
        var continuingOwner = continuing["types"]![0]!;
        continuingOwner["typeParams"]![0]!["constraints"] = new JsonArray(TypeJson.Write(Named("CurrentBound")));
        continuingOwner[KotlinSupertypesRecord.PreKey] = SourceTypeFacts.ToDom(new SourceTypeFacts(
            new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { Named("CurrentBound") } }).MergeNullableFrame(ownerFrame));
        var late = new BirNewClosure { SynthClass = new BirClosureDefinition { Name = "Late", SemanticOwner = "LiveOwner",
            TypeParams = Array.Empty<BirTypeParameter>() }, TypeArgs = Array.Empty<TypeNode>() }.ToDom();
        ((JsonArray)continuing["methods"]![0]!["body"]!).Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = late });
        PrepareSplicedOwnershipFacts(continuing, facts);
        var lateOwner = late["synthClass"]!.AsObject();
        var lateFacts = SourceTypeFacts.ReadFrozenText(lateOwner[KotlinSupertypesRecord.PreKey]!.GetValue<string>());
        if (TypeJson.Read(lateOwner["typeParams"]![0]!["constraints"]![0]) != Named("CurrentBound")
            || lateFacts.Bounds[0].Single() != Named("CurrentBound") || liveOwner.TypeParams.Value[0].Constraints.IsPresent)
            throw new InvalidOperationException("Ownership handoff read a stale owner rather than the continuing declaration");
        Console.WriteLine("[source ownership] self-test OK (prefix composition, descriptor roles, frozen facts, dense nested frames, live one-way handoff)");

        static TypeNode Tv(int index) => new TypeNode.Tv("type", index);
        static TypeNode Named(string name) => new TypeNode.Fqn(name);
        static BirTypeParameter Parameter(string name) => new() { Name = name };
        static BirClassDefinition Class(string name, params string[] parameters) => new() {
            Kind = "class", Name = name, TypeParams = parameters.Select(Parameter).ToArray(),
        };
        static BirExpressionStatement Expr(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
    }
}
