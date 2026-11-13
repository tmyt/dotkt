using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClosureSynthesis
{
    internal static void TypedMaterializedSelfTest()
    {
        var text = new TypeNode.Fqn("System.String");
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var lexical = new TypeNode.Tv("method", 2);
        var parameterFacts = new BirParameter {
            Name = "p", Type = lexical, Outer = true, Mods = new BirModifiers(),
            Default = new BirLocal { Name = "defaultRead" },
            DefaultBir = new BirDefaultBody { Index = 0, Body = new BirLocal { Name = "carriedRead" } },
            CtxFnType = 1, SharedCellType = text,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("Probe.Attribute") } },
            SuspendFnTypePre = text, FunctionSignatureType = unit, KotlinType = lexical,
            NullableGeneric = new TypeNode.Nullable(lexical), NullableFlags = new[] { 2 },
            NominalParameterSignatureIdentity = text, GenericParameterSignatureBounds = new TypeNode[] { text },
        };
        if (!parameterFacts.StructurallyEquals(ToParameter(ToLambdaParameter(parameterFacts))))
            throw new InvalidOperationException("Typed closure parameter conversion lost declaration or representation facts");
        BirNewClosure Closure(string name) => new() {
            ClosureType = new TypeNode.Fqn(name), Method = "invoke", Captures = Array.Empty<IBirExpression>(),
            FuncType = new TypeNode.Fn(false, unit, Array.Empty<TypeNode>()),
            SynthClass = new BirClosureDefinition {
                Name = name, Fields = Array.Empty<BirCaptureDescriptor>(), Params = Array.Empty<BirLambdaParameter>(),
                Ret = unit, Body = Array.Empty<IBirStatement>(),
            },
        };
        var capture = Closure("TypedMaterialized.Capture");
        var inner = Closure("TypedMaterialized.Inner");
        var outer = Closure("TypedMaterialized.Outer");
        outer.Captures = new IBirExpression[] { capture };
        outer.TypeArgs = new TypeNode[] { lexical, lexical };
        var source = outer.SynthClass.Value;
        source.TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "Duplicate" } };
        source.SemanticOwner = "TypedMaterialized.Owner";
        source.OuterTypeParamCount = 1;
        source.OuterTypeParamOffset = 1;
        source.Fields = new[] { new BirCaptureDescriptor { Name = "captured", Type = lexical, Outer = true } };
        var read = new BirFieldGet { Name = "captured", Recv = new BirThis() };
        var referenced = new BirCallStatic { Method = "selected", Ret = lexical, Sig = new TypeNode[] { lexical } };
        source.Body = new IBirStatement[] {
            new BirExpressionStatement { Expr = inner },
            new BirExpressionStatement { Expr = read },
            new BirExpressionStatement { Expr = referenced },
        };
        var statement = new BirExpressionStatement { Expr = outer };
        var method = new BirFunctionDeclaration {
            Name = "source", Body = new IBirStatement[] { statement },
            Pos = new BirDeclarationPosition { F = "/source/TypedMaterialized.kt", L = 17 },
        };
        var file = new BirFileDeclaration {
            FileClass = "TypedMaterialized.Owner", Methods = new[] { method }, Types = Array.Empty<IBirTypeDeclaration>(),
        };
        var roots = new[] { new MaterializedOccurrence(new BirGraphSlot(() => (BirGraphValue)statement.Expr.Value,
            value => statement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), ContextFor(method, new CaptureContext("<file>", null, null))) };
        var result = ApplyMaterialized(file, roots, null);
        if (!result.AppendedTypes.Select(type => type.Name.Value).SequenceEqual(new[] {
                "TypedMaterialized.Capture", "TypedMaterialized.Inner", "TypedMaterialized.Outer" })
            || !ReferenceEquals(statement.Expr.Value, outer) || outer.SynthClass.IsPresent || inner.SynthClass.IsPresent
            || !ReferenceEquals(file.Methods.Value[0], method)
            || !ReferenceEquals(file.Types.Value[^1], result.AppendedTypes[^1]))
            throw new InvalidOperationException("Typed materialized closures lost bottom-up ordering or owning occurrence identity");
        var declaration = result.AppendedTypes[^1];
        var dense = new TypeNode.Tv("type", 0);
        var invoke = declaration.Methods.Value.Single();
        var stamped = (BirFieldGet)((BirExpressionStatement)invoke.Body.Value[1]).Expr.Value;
        var formal = (BirCallStatic)((BirExpressionStatement)invoke.Body.Value[2]).Expr.Value;
        var assignment = (BirSetField)declaration.Ctors.Value.Single().Body.Value.Single();
        if (declaration.Fields.Value.Single().Type.Value != dense || stamped.Sty.Value != dense || !stamped.Outer.Value
            || formal.Ret.Value != dense || formal.Sig.Value.Single() != lexical
            || !assignment.Outer.Value || !((BirLocal)assignment.Value.Value).Outer.Value
            || declaration.OuterTypeParamOffset.Value != 1 || declaration.SemanticOwner.Value != "TypedMaterialized.Owner"
            || outer.TypeArgs.Value[0] != lexical || read.Sty.IsPresent
            || ReferenceEquals(declaration.Fields.Value.Single().Type.Value, source.Fields.Value.Single().Type.Value)
            || result.Provenance[0].Ingredients is not BirClosureDefinition snapshot
            || ((BirNewClosure)((BirExpressionStatement)snapshot.Body.Value[0]).Expr.Value).SynthClass.IsPresent != true)
            throw new InvalidOperationException("Typed closure synthesis lost generic ownership, capture stamps or independent provenance");

        var duplicate = Closure("TypedMaterialized.Outer");
        var duplicateStatement = new BirExpressionStatement { Expr = duplicate };
        var duplicateResult = ApplyMaterialized(file, new[] { new MaterializedOccurrence(
            new BirGraphSlot(() => (BirGraphValue)duplicateStatement.Expr.Value,
                value => duplicateStatement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), roots[0].Context) }, null);
        if (duplicateResult.AppendedTypes.Count != 0 || duplicate.SynthClass.IsPresent)
            throw new InvalidOperationException("Typed closure synthesis changed declaration-name deduplication");

        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            TypedFileSelfTest(refs);
            var target = new TypeNode.Fqn("System.Action");
            var sam = new BirNewSam {
                SamType = new TypeNode.Fqn("TypedMaterialized.Delegate"), Captures = Array.Empty<IBirExpression>(),
                SynthClass = new BirClassDefinition {
                    Name = "TypedMaterialized.Delegate", Fields = Array.Empty<BirFieldDeclaration>(),
                    Interfaces = new TypeNode[] { target }, Methods = new[] { new BirFunctionDeclaration {
                        Name = "Invoke", Params = Array.Empty<BirParameter>(), Ret = unit, Body = Array.Empty<IBirStatement>(),
                    } },
                },
            };
            var conversion = new BirSamConversion { Type = target, E = sam };
            var samStatement = new BirExpressionStatement { Expr = conversion };
            var samResult = ApplyMaterialized(file, new[] { new MaterializedOccurrence(
                new BirGraphSlot(() => (BirGraphValue)samStatement.Expr.Value,
                    value => samStatement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), roots[0].Context) }, refs);
            // A nonliteral samConvert remains explicit, while its SAM operand
            // becomes a closure and carries the exact projected delegate slot.
            if (!ReferenceEquals(samStatement.Expr.Value, conversion)
                || conversion.E.Value is not BirNewClosure normalized || normalized.DotktDelegateSlot.Value != target
                || normalized.FuncType.Value is not TypeNode.Fn { Ret: TypeNode.Fqn { Name: "kotlin.Unit" } }
                || samResult.Provenance.Single().Ingredients is not BirClosureDefinition)
                throw new InvalidOperationException("Typed CLR SAM normalization lost its explicit conversion or closure-shaped provenance");
            var literal = Closure("TypedMaterialized.Literal");
            var literalStatement = new BirExpressionStatement { Expr = new BirSamConversion { Type = target, E = literal } };
            ApplyMaterialized(file, new[] { new MaterializedOccurrence(
                new BirGraphSlot(() => (BirGraphValue)literalStatement.Expr.Value,
                    value => literalStatement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), roots[0].Context) }, refs);
            if (!ReferenceEquals(literalStatement.Expr.Value, literal) || literal.DotktDelegateSlot.Value != target
                || !ClrMemberResolution.MarkDelegateSlot(literal, text, refs, new HashSet<string>()))
                throw new InvalidOperationException("Typed delegate marking lost literal retargeting or an existing slot");
            var replacementTarget = new TypeNode.Fqn("System.Func`1", new TypeNode[] { text });
            ClrMemberResolution.MarkDelegateSlot(literal, replacementTarget, refs, new HashSet<string>());
            if (literal.DotktDelegateSlot.Value != replacementTarget)
                throw new InvalidOperationException("Typed delegate marking lost last-writer-wins selection");

            var illegal = Closure("TypedMaterialized.IllegalCapture");
            illegal.SynthClass.Value.Fields = new[] { new BirCaptureDescriptor {
                Name = "span", Type = new TypeNode.Fqn("System.Span`1", new TypeNode[] { new TypeNode.Fqn("System.Int32") }),
            } };
            var illegalStatement = new BirExpressionStatement { Expr = illegal };
            var captureFile = new BirFileDeclaration {
                FileClass = "TypedMaterialized.CaptureOwner", Types = Array.Empty<IBirTypeDeclaration>(),
            };
            var refused = ApplyMaterialized(captureFile, new[] { new MaterializedOccurrence(
                new BirGraphSlot(() => (BirGraphValue)illegalStatement.Expr.Value,
                    value => illegalStatement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), roots[0].Context) }, refs);
            // Check the real surviving-type gate after the one typed-to-DOM
            // frontier, rather than introducing a second test-only registry.
            var refusedAtFinalGate = false;
            try { AssertSurvivingCapturesLegal(new[] { captureFile.ToDom() }); }
            catch (NotSupportedException error)
            {
                refusedAtFinalGate = error.Message.Contains("TypedMaterialized.kt:17: closure-synthesis: in `source`")
                    && error.Message.Contains("`span`") && error.Message.Contains("TypedMaterialized.IllegalCapture");
            }
            if (!refusedAtFinalGate) throw new InvalidOperationException("Typed capture refusal did not reach the shared surviving-type gate");
            captureFile.Types = Array.Empty<IBirTypeDeclaration>();
            AssertSurvivingCapturesLegal(new[] { captureFile.ToDom() });
        }
        finally { refs.DisposeNet(); }
        Console.WriteLine("[typed materialized closures] self-test OK (owning slots, bottom-up classes, generic frames, provenance and delegate slots)");
    }
}
