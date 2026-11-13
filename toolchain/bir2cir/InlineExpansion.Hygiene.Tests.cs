using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal static void SelfTestHygiene()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var engine = new InlineExpansion(new InlinePassContext {
            File = new BirFileDeclaration(), References = null, Bodies = null, Dispatch = null,
            CompanionExtensions = null, NextSpliceIdentity = () => 1, AppLocalMethods = new(StringComparer.Ordinal), NextLabelId = 40,
        });
        var label = new BirLabel { Id = 2 };
        var jump = new BirGoto { Id = 2 };
        var external = new BirGoto { Id = 99 };
        var result = new BirValueBlock { Stmts = new IBirStatement[] { jump, external }, Result = OptionalField<IBirExpression>.Of(UnitConst()) };
        var declared = new BirValueBlock { Stmts = new IBirStatement[] { label }, Result = OptionalField<IBirExpression>.Of(UnitConst()) };
        engine.FreshenLabelsJoint(declared, result);
        if (label.Id.Value != 40 || jump.Id.Value != 40 || external.Id.Value != 99 || engine.Pass.NextLabelId != 41)
            throw new InvalidOperationException("Inline joint label hygiene changed an external branch or split its region map");

        var outer = new BirVariable { Name = "x", Type = integer };
        var capturedRead = new BirLocal { Name = "x", Sty = integer };
        var parameterRead = new BirLocal { Name = "x", Sty = integer };
        var nested = new BirInlineLambda {
            Params = new[] { new BirLambdaParameter { Name = "x", Type = integer } },
            Captures = new[] { new BirInlineCapture { Name = "free", Type = integer, Value = OptionalField<IBirExpression>.Of(capturedRead) } },
            Body = Array.Empty<IBirStatement>(), Result = OptionalField<IBirExpression>.Of(parameterRead),
        };
        var suspendRead = new BirLocal { Name = "x", Sty = integer };
        var constructionRead = new BirLocal { Name = "x", Sty = integer };
        var suspend = new BirNewSuspendLambda {
            Captures = new[] { new BirCaptureDescriptor { Name = "x", Type = integer } },
            Params = Array.Empty<BirLambdaParameter>(),
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(suspendRead) } },
            CapValues = new IBirExpression[] { constructionRead },
        };
        var region = new BirValueBlock {
            Stmts = new IBirStatement[] { outer, new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(nested) } },
            Result = OptionalField<IBirExpression>.Of(suspend),
        };
        engine.PrefixLocalsJoint("clone$", region);
        if (outer.Name.Value != "clone$x" || parameterRead.Name.Value != "x" || capturedRead.Name.Value != "clone$x"
            || suspend.Captures.Value[0].Name.Value != "clone$x" || suspendRead.Name.Value != "clone$x" || constructionRead.Name.Value != "clone$x")
            throw new InvalidOperationException("Inline local hygiene lost parameter shadowing or construction/capture agreement");

        var transfer = new BirReturnExpression { Value = OptionalField<IBirExpression>.Of(new BirConstant {
            Type = integer, Value = new BirLiteral.IntegerLiteral(7),
        }) };
        var branch = new BirValueBlock { Result = OptionalField<IBirExpression>.Of(transfer) };
        var routed = engine.RouteReturns(new IBirStatement[] {
            new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(branch) },
            new BirReturn { Value = OptionalField<IBirExpression>.Of(new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(8) }) },
        }, false, integer, "r$");
        if (branch.Stmts.IsPresent || branch.Result.Value is not BirValueBlock expressionTransfer
            || expressionTransfer.Stmts.Value is not [BirSetLocal, BirGoto]
            || routed.Body is not [BirVariable, BirExpressionStatement, BirSetLocal, BirGoto, BirLabel]
            || ((BirGoto)expressionTransfer.Stmts.Value[1]).Id.Value != ((BirLabel)routed.Body[^1]).Id.Value)
            throw new InvalidOperationException("Inline return routing lost expression ownership, vector expansion or absent statement facts");

        var call = new BirCallStatic {
            Sig = new TypeNode[] { new TypeNode.Tv("method", 9) },
            ResolvedMemberReturn = new TypeNode.Tv("method", 10),
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 2) }, Ret = new TypeNode.Tv("method", 3),
        };
        var signature = call.Sig.Value;
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(call, keys);
        if (!keys.SetEquals(new[] { ("method", 2), ("method", 3) }) || !ReferenceEquals(signature, call.Sig.Value))
            throw new InvalidOperationException("Inline caller TV collection entered foreign descriptors or mutated their vector identity");
        RenumberTvs(call, new Dictionary<(string, int), int> { [("method", 2)] = 0, [("method", 3)] = 1 }, true);
        if (call.TypeArgs.Value[0] != new TypeNode.Tv("type", 0) || call.Ret.Value != new TypeNode.Tv("type", 1)
            || call.ResolvedMemberReturn.Value != new TypeNode.Tv("method", 10) || call.Sig.Value[0] != new TypeNode.Tv("method", 9))
            throw new InvalidOperationException("Inline caller TV remap changed a selected declaration's independent frame");
        var cell = new TypeNode.Fqn("Cell");
        var request = new InlineBoxRequest("storage", cell.Name, cell, integer, false, new(StringComparer.Ordinal) { "storage" });
        var suspendCapture = new BirNewSuspendLambda {
            Captures = new[] { new BirCaptureDescriptor { Name = "storage", Type = cell } },
        };
        if (!MaterializedCaptureSources(suspendCapture, request).SequenceEqual(new[] { "storage" }))
            throw new InvalidOperationException("Inline boxing lost an implicit suspend capture operand");
        suspendCapture.CapValues = new IBirExpression[] { null };
        if (!MaterializedCaptureSources(suspendCapture, request).SequenceEqual(new[] { "storage" }))
            throw new InvalidOperationException("Inline boxing lost a null suspend capture operand");
        suspendCapture.CapValues = new IBirExpression[] { new BirLocal { Name = "source" } };
        if (!MaterializedCaptureSources(suspendCapture, request).SequenceEqual(new[] { "source" }))
            throw new InvalidOperationException("Inline boxing ignored an explicit suspend capture's source");
        suspendCapture.CapValues = new IBirExpression[] { new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) } };
        if (MaterializedCaptureSources(suspendCapture, request).Any())
            throw new InvalidOperationException("Inline boxing inferred a source for a non-local explicit operand");

        var normalizer = new InlineExpansion(new InlinePassContext {
            File = new BirFileDeclaration(), References = ReferenceMetadataIndex.Build(Array.Empty<string>()),
            Bodies = null, Dispatch = null, CompanionExtensions = null,
            NextSpliceIdentity = () => 1, AppLocalMethods = new(StringComparer.Ordinal),
        });
        foreach (var makeBody in new Func<IBirStatement[], BirGraphValue>[] {
            body => new BirBlock { Body = body }, body => new BirForArray { Body = body },
            body => new BirForIn { Body = body }, body => new BirFor { Body = body },
            body => new BirForRange { Body = body }, body => new BirForEach { Body = body },
            body => new BirRepeat { Body = body },
        })
        {
            var destination = new BirVariable {
                Name = "result", Type = integer,
                Init = OptionalField<IBirExpression>.Of(new BirLocal { Name = "__inl$value" }),
            };
            var initializerBody = makeBody(new IBirStatement[] {
                new BirVariable { Name = "__inl$value", Type = new TypeNode.Nullable(integer) }, destination,
            });
            var initializer = new BirValueBlock { Stmts = new[] { (IBirStatement)initializerBody } };
            normalizer.NormalizeImplicitNullableUnwrap(new BirFileDeclaration {
                Fields = new[] { new BirFieldDeclaration { Init = OptionalField<IBirExpression>.Of(initializer) } },
            });
            if (destination.Init.Value is not BirNullableValue { Elem.HasValue: true } unwrapped || unwrapped.Elem.Value != integer)
                throw new InvalidOperationException("Inline nullable normalization missed an initializer's " + ((BirGraphNode)initializerBody).Tag + " body");
        }
        Console.WriteLine("[typed inline hygiene] self-test OK (labels, captures, shadowing, return regions, TV roles, suspend cells, initializer bodies)");
    }
}
