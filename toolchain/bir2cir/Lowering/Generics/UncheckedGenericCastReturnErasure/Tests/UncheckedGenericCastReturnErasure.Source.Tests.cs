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
        static SourceDeclarationProjection Ledger(IReadOnlyList<BirFileDeclaration> roots)
            => new(roots, new CurrentDeclarationBindings(roots));
        static void Complete(PendingSourceIndex pending, IReadOnlyList<BirFileDeclaration> roots, SourceDeclarationProjection ledger)
        {
            ledger.SealSourceContinuations();
            CompleteSourceDeclarations(pending, roots, null, ledger);
        }
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
            var ledger = Ledger(source);
            var capturedIndex = pending.SourceDeclarations;
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
            Complete(pending, source, ledger);
            Assert(other.Ret.Value == new TypeNode.Fqn("object")
                && ((BirCast)currentReturn.Value.Value).Type.Value == new TypeNode.Fqn("object")
                && capturedIndex.ByIdentity[other.DeclarationId.Value].Return == sourceReturn
                && ReferenceEquals(capturedRow, capturedIndex.ByIdentity[other.DeclarationId.Value]) && capturedRow.SourceMethod == null,
                "bound body edit or frozen pre-lowering return was lost");
            Refuses(() => CompleteSourceDeclarations(pending, source, null, ledger), "a pending association completed twice");
        }

        var detachedSource = new[] { Root("Detached", "detached") };
        var detachedPending = CaptureSource(detachedSource);
        var detachedLedger = Ledger(detachedSource);
        var bound = detachedSource[0].Methods.Value[0];
        var replacement = (BirFunctionDeclaration)bound.DeepClone();
        detachedSource[0].Methods.Value[0] = replacement;
        Apply(detachedSource[0], detachedPending);
        Assert(bound.Ret.Value == Tv() && replacement.Ret.Value == Tv()
            && ReferenceEquals(detachedPending.SourceDeclarations.ByIdentity["detached"].SourceMethod, bound),
            "detached occurrence was mutated or its replacement was chased by identity");
        Refuses(() => Complete(detachedPending, detachedSource, detachedLedger), "a replacement rebound by its copied identity");

        // Witness synthesis explicitly retires actual generated targets. Only
        // those reports may shift a later captured declaration's owning slot.
        var prefix = new BirFunctionDeclaration { Name = "prefix", DeclarationId = "prefix-id", Generated = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() };
        var retiredTarget = Method("retired-target");
        retiredTarget.Generated = true;
        var survivor = Method("survivor");
        var retirementSource = new[] { new BirFileDeclaration { FileClass = "Retirement",
            Methods = new[] { prefix, retiredTarget, survivor } } };
        var retirementPending = CaptureSource(retirementSource);
        var retirementLedger = Ledger(retirementSource);
        var retirementIndex = retirementPending.SourceDeclarations;
        retirementLedger.RetireForest(prefix); retirementLedger.RetireForest(retiredTarget);
        retirementSource[0].Methods = new[] { survivor, new BirFunctionDeclaration { Name = "appended-helper" } };
        Apply(retirementSource[0], retirementPending);
        Complete(retirementPending, retirementSource, retirementLedger);
        Assert(retirementIndex.ByIdentity["survivor"].SourceMethod == null
            && retirementIndex.ByIdentity["retired-target"].SourceMethod == null
            && ReferenceEquals(retirementSource[0].Methods.Value[0], survivor)
            && retirementLedger.IsRetired(retiredTarget) && retirementLedger.IsRetired(prefix),
            "reported retirement lost its exact detached occurrences or shifted by scalar identity");
        Assert(survivor.Ret.Value == new TypeNode.Fqn("object") && retiredTarget.Ret.Value == Tv(),
            "explicit retired declaration was mutated despite lacking continuing root ownership");

        var unreportedSource = new[] { new BirFileDeclaration { FileClass = "Unreported",
            Methods = new[] { prefix, Method("still-exact") } } };
        var unreportedPending = CaptureSource(unreportedSource);
        var unreportedLedger = Ledger(unreportedSource);
        unreportedSource[0].Methods = new[] { unreportedSource[0].Methods.Value[1] };
        Refuses(() => Complete(unreportedPending, unreportedSource, unreportedLedger),
            "an unreported preceding deletion silently shifted the selected occurrence");

        var pair = new[] { Root("A", "a"), Root("B", "b") };
        var wrongOrder = CaptureSource(pair);
        var wrongOrderLedger = Ledger(pair);
        Refuses(() => Complete(wrongOrder, pair.Reverse().ToArray(), wrongOrderLedger), "file order silently rebound");
        foreach (var mismatch in new[] { "identity", "static", "ordinal", "source", "return" })
        {
            var files = new[] { Root("Mismatch", "exact") };
            var pending = CaptureSource(files);
            var ledger = Ledger(files);
            var method = files[0].Methods.Value[0];
            if (mismatch == "identity") method.DeclarationId = "different";
            else if (mismatch == "static") method.Static = false;
            else if (mismatch == "ordinal") files[0].Methods = new[] { new BirFunctionDeclaration { Name = "read" }, method };
            else if (mismatch == "return") method.Ret = new TypeNode.Fqn("object");
            else files[0].Methods.Value[0] = (BirFunctionDeclaration)method.DeepClone();
            Refuses(() => Complete(pending, files, ledger), "lost exact " + mismatch + " association");
        }
        var typeSource = new[] { new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { Method("member") } },
        } } };
        var validMember = CaptureSource(typeSource);
        var validLedger = Ledger(typeSource);
        var memberIndex = validMember.SourceDeclarations;
        var actualMember = typeSource[0].Types.Value[0].Methods.Value[0];
        Complete(validMember, typeSource, validLedger);
        Assert(memberIndex.ByIdentity["member"].Owner == "Owner" && !memberIndex.ByIdentity["member"].TopLevel
            && memberIndex.ByIdentity["member"].SourceMethod == null
            && ReferenceEquals(actualMember, typeSource[0].Types.Value[0].Methods.Value[0]),
            "member occurrence did not retain its explicit owner axis");
        var memberPending = CaptureSource(typeSource);
        var memberLedger = Ledger(typeSource);
        typeSource[0].Types.Value[0].Name = "OtherOwner";
        Refuses(() => Complete(memberPending, typeSource, memberLedger), "type owner axis silently rebound");

        // Declaration-relative source discovery is deliberately narrower than
        // static-type inference, but not narrower than the former body walker.
        bool Captures(IBirStatement statement)
        {
            var file = Root("Detection", "detect");
            file.Methods.Value[0].Body = new[] { statement };
            var roots = new[] { file };
            return CaptureSource(roots).SourceDeclarations.ByIdentity.Count != 0;
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
        SelfTestReferencedUses();
        Console.WriteLine("[unchecked cast ownership] self-test OK (typed capture, exact one-shot completion, both root orders, live bodies, detach, explicit source types)");
    }
}
