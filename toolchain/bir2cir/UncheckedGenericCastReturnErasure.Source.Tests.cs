using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    internal static void SelfTest()
    {
        static TypeNode.Tv Tv() => new("method", 0);
        static BirCast Cast(IBirExpression source = null) => new() {
            Type = Tv(), ReifiedTypeOperand = false,
            E = OptionalField<IBirExpression>.Of(source ?? new BirLocal { Name = "value",
                Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) }),
        };
        static BirFunctionDeclaration Method(string identity) => new() {
            Name = "read", DeclarationId = identity, Static = true, Ret = Tv(),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(Cast()) } },
        };
        static BirFileDeclaration Root(string owner, string identity) => new() {
            FileClass = owner, Methods = new[] { Method(identity) },
        };
        static JsonObject[] Project(IReadOnlyList<BirFileDeclaration> roots) => roots.Select(root => root.ToDom()).ToArray();
        static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Unchecked typed snapshot: " + message); }
        static void Refuses(Action action, string message)
        {
            bool refused = false;
            try { action(); } catch (InvalidOperationException) { refused = true; }
            Assert(refused, message);
        }

        foreach (var reverse in new[] { false, true })
        {
            var first = Root("First", "first");
            var second = Root("Second", "second");
            if (reverse) (first, second) = (second, first);
            var sourceReturn = second.Methods.Value[0].Ret.Value;
            first.Methods = first.Methods.Value.Append(new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
                Body = new IBirStatement[] { new BirVariable { Name = "deferred", Type = Tv(),
                    Init = OptionalField<IBirExpression>.Of(new BirCallStatic {
                        Method = "read", DeclarationId = second.Methods.Value[0].DeclarationId,
                        Owner = new TypeNode.Fqn(second.FileClass.Value), TypeArgs = new TypeNode[] { Tv() },
                        Args = Array.Empty<IBirExpression>(),
                    }) } },
            }).ToArray();
            var source = new[] { first, second };
            var pending = CaptureSource(source);
            var capturedRow = pending.SourceDeclarations.ByIdentity[second.Methods.Value[0].DeclarationId.Value];
            var other = second.Methods.Value[0];
            Apply(first, pending);
            Assert(first.Methods.Value[0].Ret.Value == new TypeNode.Fqn("object")
                && ((BirVariable)first.Methods.Value[1].Body.Value[0]).Type.Value == new TypeNode.Fqn("object")
                && other.Ret.Value == sourceReturn
                && ((BirCast)((BirReturn)other.Body.Value[0]).Value.Value).Type.Value == sourceReturn,
                "lowering crossed root ownership or lost cross-file ABI facts");
            // Read the current owning body, never a capture-time body clone.
            var currentReturn = new BirReturn { Value = OptionalField<IBirExpression>.Of(Cast()) };
            other.Body = new IBirStatement[] { currentReturn };
            Apply(second, pending);
            var roots = Project(source);
            var index = BindDomDeclarations(pending, roots);
            Assert(other.Ret.Value == new TypeNode.Fqn("object")
                && ((BirCast)currentReturn.Value.Value).Type.Value == new TypeNode.Fqn("object")
                && index.ByIdentity[other.DeclarationId.Value].Return == sourceReturn
                && ReferenceEquals(capturedRow, index.ByIdentity[other.DeclarationId.Value]) && capturedRow.SourceMethod == null,
                "bound body edit or frozen pre-lowering return was lost");
            Refuses(() => BindDomDeclarations(pending, roots), "a pending association bound twice");
        }

        var detachedSource = new[] { Root("Detached", "detached") };
        var detachedPending = CaptureSource(detachedSource);
        var bound = detachedSource[0].Methods.Value[0];
        var replacement = (BirFunctionDeclaration)bound.DeepClone();
        detachedSource[0].Methods.Value[0] = replacement;
        Apply(detachedSource[0], detachedPending);
        Assert(bound.Ret.Value == Tv() && replacement.Ret.Value == Tv()
            && ReferenceEquals(detachedPending.SourceDeclarations.ByIdentity["detached"].SourceMethod, bound),
            "detached occurrence was mutated or its replacement was chased by identity");
        Refuses(() => BindDomDeclarations(detachedPending, Project(detachedSource)), "a replacement rebound by its copied identity");

        // Witness synthesis explicitly retires actual generated targets. Only
        // those reports may shift a later captured declaration's owning slot.
        var prefix = new BirFunctionDeclaration { Name = "prefix", DeclarationId = "survivor", Generated = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
        var retiredTarget = Method("retired-target");
        retiredTarget.Generated = true;
        var survivor = Method("survivor");
        var retirementSource = new[] { new BirFileDeclaration { FileClass = "Retirement",
            Methods = new[] { prefix, retiredTarget, survivor } } };
        var retirementPending = CaptureSource(retirementSource);
        retirementSource[0].Methods = new[] { survivor, new BirFunctionDeclaration { Name = "appended-helper" } };
        Apply(retirementSource[0], retirementPending);
        var retirementRoots = Project(retirementSource);
        var retiredProjection = retiredTarget.ToDom();
        var retirements = new Dictionary<BirFunctionDeclaration, JsonObject>(ReferenceEqualityComparer.Instance) {
            [prefix] = prefix.ToDom(), [retiredTarget] = retiredProjection,
        };
        var retirementIndex = BindDomDeclarations(retirementPending, retirementRoots, retirements);
        Assert(ReferenceEquals(retirementIndex.ByIdentity["survivor"].Method, retirementRoots[0]["methods"][0])
            && ReferenceEquals(retirementIndex.ByIdentity["retired-target"].Method, retiredProjection)
            && retiredProjection.Parent == null, "reported retirement lost its shared detached projection or shifted by scalar identity");
        Assert(TypeJson.Read(retirementRoots[0]["methods"][0]["ret"]) == new TypeNode.Fqn("object")
            && TypeJson.Read(retiredProjection["ret"]) == Tv(),
            "explicit retired declaration was mutated despite lacking continuing root ownership");

        var unreportedSource = new[] { new BirFileDeclaration { FileClass = "Unreported",
            Methods = new[] { prefix, Method("still-exact") } } };
        var unreportedPending = CaptureSource(unreportedSource);
        unreportedSource[0].Methods = new[] { unreportedSource[0].Methods.Value[1] };
        Refuses(() => BindDomDeclarations(unreportedPending, Project(unreportedSource)),
            "an unreported preceding deletion silently shifted the selected occurrence");

        var pair = new[] { Root("A", "a"), Root("B", "b") };
        var wrongOrder = CaptureSource(pair);
        Refuses(() => BindDomDeclarations(wrongOrder, Project(pair).Reverse().ToArray()), "file order silently rebound");
        foreach (var mismatch in new[] { "identity", "static", "ordinal", "source" })
        {
            var files = new[] { Root("Mismatch", "exact") };
            var pending = CaptureSource(files);
            var roots = Project(files);
            var method = roots[0]["methods"][0].AsObject();
            if (mismatch == "identity") method[DeclarationIdentityBinding.Key] = "different";
            else if (mismatch == "static") method["static"] = false;
            else if (mismatch == "ordinal") roots[0]["methods"].AsArray().Insert(0, new JsonObject { ["name"] = "read" });
            else files[0].Methods.Value[0] = (BirFunctionDeclaration)files[0].Methods.Value[0].DeepClone();
            Refuses(() => BindDomDeclarations(pending, roots), "lost exact " + mismatch + " association");
        }
        var typeSource = new[] { new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { Method("member") } },
        } } };
        var validMember = CaptureSource(typeSource);
        var validMemberRoots = Project(typeSource);
        var memberIndex = BindDomDeclarations(validMember, validMemberRoots);
        Assert(memberIndex.ByIdentity["member"].Owner == "Owner" && !memberIndex.ByIdentity["member"].TopLevel
            && ReferenceEquals(memberIndex.ByIdentity["member"].Method, validMemberRoots[0]["types"][0]["methods"][0]),
            "member occurrence did not retain its explicit owner axis");
        var memberPending = CaptureSource(typeSource);
        var memberRoots = Project(typeSource);
        memberRoots[0]["types"][0]["name"] = "OtherOwner";
        Refuses(() => BindDomDeclarations(memberPending, memberRoots), "type owner axis silently rebound");

        // Declaration-relative source discovery is deliberately narrower than
        // static-type inference, but not narrower than the former body walker.
        bool Captures(IBirStatement statement)
        {
            var file = Root("Detection", "detect");
            file.Methods.Value[0].Body = new[] { statement };
            var roots = new[] { file };
            return BindDomDeclarations(CaptureSource(roots), Project(roots)).ByIdentity.Count != 0;
        }
        Assert(!Captures(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirNewClosure {
            SynthClass = new BirClosureDefinition { Body = Method("inner").Body },
        }) }), "synthClass return leaked into its caller ABI");
        Assert(Captures(new BirLocalFunction { Decl = Method("local") }), "local-function body was newly excluded");
        Assert(Captures(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirNewSuspendLambda {
            Body = Method("suspend").Body,
        }) }), "suspend body was newly excluded");
        Assert(!Captures(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirReturnExpression {
            Value = OptionalField<IBirExpression>.Of(Cast()),
        }) }), "returnExpr was mistaken for return");
        Assert(!Captures(new BirReturn { Value = OptionalField<IBirExpression>.Of(Cast(new BirCallStatic {
            DynRet = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
        })) }), "dynamic return was used as an explicit source type");
        Assert(!Captures(new BirReturn { Value = OptionalField<IBirExpression>.Of(Cast(new BirCallStatic {
            Sty = new TypeNode.Fqn("kotlin.String"), Ret = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
        })) }), "ret overrode explicit sty precedence");
        SelfTestCallableFlow();
        Console.WriteLine("[unchecked cast ownership] self-test OK (typed capture, exact one-shot binding, both root orders, live bodies, detach, explicit source types)");
    }
}
