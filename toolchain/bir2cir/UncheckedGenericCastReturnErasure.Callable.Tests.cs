using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    static void SelfTestCallableFlow()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var erased = new TypeNode.Fqn("object");
        var text = new TypeNode.Fqn("kotlin.String");
        var variable = new TypeNode.Tv("method", 0);
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Typed callable erasure: " + message); }
        BirFunctionDeclaration Target(string name, TypeNode result) => new() { Name = name, DeclarationId = name,
            Generated = true, Static = true, SemanticOwner = "Owner", Ret = result,
            Params = new[] { new BirParameter { Name = "value", Type = nullable } },
            Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "value", Sty = nullable } } } };
        BirVariable Bind(string name, TypeNode result, string method, TypeNode[] signature = null) => new() { Name = name,
            Init = new BirNewDelegate { Method = method, CalleeOwner = new TypeNode.Fqn("Owner"),
                FuncType = new TypeNode.Fn(false, result, new TypeNode[] { erased }),
                Sig = signature == null ? default : OptionalField<TypeNode[]>.Of(signature) } };
        var target = Target("lifted", nullable);
        var originalParameter = target.Params.Value[0];
        var referenceTarget = Target("reference", text);
        var direct = Target("direct", nullable);
        var invoke = Target("invoke", nullable); invoke.Static = false;
        var closureType = new BirClassDefinition { Name = "Closure", Kind = "class", Methods = new[] { invoke } };
        var caller = new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] {
            Bind("a", erased, "lifted"), Bind("b", erased, "reference"), Bind("c", erased, "direct", new TypeNode[] { nullable }),
            new BirVariable { Name = "closure", Init = new BirNewClosure { ClosureType = new TypeNode.Fqn("Closure"),
                FuncType = new TypeNode.Fn(false, erased, new TypeNode[] { erased }) } },
        } };
        var root = new BirFileDeclaration { FileClass = "Facade", Methods = new[] { target, referenceTarget, direct, caller },
            Types = new IBirTypeDeclaration[] { closureType } };
        var pending = CaptureSource(new[] { root });
        Require(pending.AlignDelegateTargets(root, type => type.Name == "kotlin.Int"), "alignment did not report moved declarations");
        Require(ReferenceEquals(root.Methods.Value[0], target) && ReferenceEquals(target.Params.Value[0], originalParameter)
            && target.Ret.Value == erased && originalParameter.Type.Value == erased && originalParameter.Name.Value != "value"
            && target.Body.Value[0] is BirVariable { Name.Value: "value", Init.Value: BirCast {
                _exactBridgeCast.Value: true, FunctionValueRestoration.Value: true, E.Value: BirLocal physical } }
            && physical.Name.Value == originalParameter.Name.Value && physical.Sty.Value == erased,
            "alignment lost its owning parameter or exact restoration prologue");
        Require(referenceTarget.Ret.Value == text && direct.Ret.Value == nullable && direct.Params.Value[0].Type.Value == nullable
            && ReferenceEquals(root.Types.Value[0], closureType) && invoke.Ret.Value == erased && invoke.Params.Value[0].Type.Value == erased,
            "reference returns, selected signatures or closure declarations changed their contract");
        var declarations = NullableTvErasureCallRealign.CollectDeclaredMemberRets(new[] { root });
        NullableTvErasureCallRealign.ApplySourceUses(root, declarations, type => type.Name == "kotlin.Int", null);
        Require(((BirReturn)target.Body.Value[1]).Value.Value is BirCast { Type.Value: var returnTarget } && returnTarget == erased,
            "moved declaration was not reflowed by the typed nullable-use capability");
        Require(!pending.AlignDelegateTargets(root, type => type.Name == "kotlin.Int"), "alignment is not idempotent");

        var ambiguousA = Target("ambiguous", nullable);
        var ambiguousB = Target("ambiguous", nullable); ambiguousB.DeclarationId = "other";
        ambiguousB.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var ambiguousRoot = new BirFileDeclaration { FileClass = "Facade", Methods = new[] { ambiguousA, ambiguousB,
            new BirFunctionDeclaration { Name = "bind", Body = new IBirStatement[] { Bind("x", erased, "ambiguous") } } } };
        Require(!CaptureSource(new[] { ambiguousRoot }).AlignDelegateTargets(ambiguousRoot, _ => true)
            && ambiguousA.Ret.Value == nullable && ambiguousB.Ret.Value == nullable, "ambiguous wildcard selected a declaration");

        BirFunctionDeclaration Unchecked(string name) => new() { Name = name, DeclarationId = name, Static = true,
            Generated = true, Ret = variable, Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = variable,
                E = new BirLocal { Name = "input", Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) } } } } };
        var reported = Unchecked("reported");
        var reportedRoot = new BirFileDeclaration { FileClass = "Reported", Methods = new[] { reported,
            new BirFunctionDeclaration { Name = "bind", Body = new IBirStatement[] {
                new BirVariable { Name = "function", Init = new BirNewDelegate { Method = "reported", CalleeOwner = new TypeNode.Fqn("Reported"),
                    FuncType = new TypeNode.Fn(false, erased, Array.Empty<TypeNode>()) } } } } } };
        var reportedPending = CaptureSource(new[] { reportedRoot });
        var row = reportedPending.SourceDeclarations.ByIdentity["reported"];
        Require(reportedPending.AlignDelegateTargets(reportedRoot, _ => true) && reported.Ret.Value == erased
            && row.Return == variable, "reported physical alignment changed the captured body variable");
        Apply(reportedRoot, reportedPending);
        var bound = BindDomDeclarations(reportedPending, new[] { reportedRoot.ToDom() });
        Require(ReferenceEquals(bound.ByIdentity["reported"], row) && row.SourceMethod == null && row.Method != null
            && row.Return == variable && TypeJson.Read(row.Method["ret"]) == erased, "physical signature report lost its exact single-row handoff");
        foreach (var tamperAfterReport in new[] { false, true })
        {
            var source = (BirFileDeclaration)reportedRoot.DeepClone();
            source.Methods.Value[0].Ret = variable;
            var captured = CaptureSource(new[] { source });
            if (tamperAfterReport) captured.AlignDelegateTargets(source, _ => true);
            source.Methods.Value[0].Ret = text;
            var refused = false;
            try { BindDomDeclarations(captured, new[] { source.ToDom() }); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "unreported return mutation passed exact physical association");
        }

        var read = Unchecked("read");
        var call = new BirCallStatic { Method = "read", DeclarationId = "read", Owner = new TypeNode.Fqn("Flow"),
            TypeArgs = new TypeNode[] { variable }, Args = Array.Empty<IBirExpression>(), Sty = variable };
        var deferred = new BirVariable { Name = "value", Type = variable, Init = call };
        var argument = new BirLocal { Name = "value", Sty = variable };
        var sink = new BirCallStatic { Method = "sink", Sig = new TypeNode[] { variable }, Args = new IBirExpression[] { argument } };
        var user = new BirFunctionDeclaration { Name = "user", Body = new IBirStatement[] { deferred, new BirExpressionStatement { Expr = sink } } };
        var flowRoot = new BirFileDeclaration { FileClass = "Flow", Methods = new[] { read, user } };
        var flowPending = CaptureSource(new[] { flowRoot });
        Apply(flowRoot, flowPending);
        Require(deferred.Type.Value == erased && call.Ret.Value == erased && call.Sty.Value == erased
            && sink.Args.Value[0] is BirCast { Type.Value: var narrowed, E.Value: BirLocal inner } && narrowed == variable
            && ReferenceEquals(inner, argument) && read.RetKotlinType.Value == variable,
            "deferred object transport or exact typed consumer narrowing changed");
        var once = flowRoot.DeepClone(); Apply(flowRoot, flowPending);
        Require(flowRoot.StructurallyEquals(once), "unchecked typed initial pass is not idempotent");
        Console.WriteLine("[typed callable erasure] self-test OK (target identity, exact restoration, variance, ambiguity, shared physical handoff, deferred consumers)");
    }
}
