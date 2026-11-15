using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    // Both actual owning entries consume the same saved-bound policy. The
    // independent authored inputs differ only in file versus detached-method scope.
    static void NestedBoundGuardSelfTest(ValueRepresentationLowering policy)
    {
        var cases = new List<(int Depth, BirFunctionDeclaration Source, BirFunctionDeclaration Materialized)>();
        var variable = new TypeNode.Tv("method", 0);
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        foreach (var depth in new[] { 2, 3, 4 })
        {
            IBirExpression operand = new BirLocal { Name = "value", Sty = any };
            for (var index = 0; index < depth; index++)
                operand = new BirCast { Type = variable, ReifiedTypeOperand = false,
                    E = OptionalField<IBirExpression>.Of(operand) };
            var method = new BirFunctionDeclaration {
                Name = "nestedBoundGuard" + depth, Ret = variable,
                Params = new[] { new BirParameter { Name = "value", Type = any } },
                TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = Array.Empty<TypeNode>() } },
                MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                    [0] = new TypeNode[] { new TypeNode.Fqn("Union") } }),
                Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(operand) } },
            };
            var materialized = (BirFunctionDeclaration)method.DeepClone();
            policy.BindOperations(new BirFileDeclaration { Methods = new[] { method } });
            cases.Add((depth, method, materialized));
        }
        var differences = new List<string>();
        foreach (var (depth, source, materialized) in cases)
        {
            policy.BindMaterializedOperations(materialized);
            var sourceCounts = Count(source);
            var materializedCounts = Count(materialized);
            var report = $"depth={depth}: typed nodes={sourceCounts.Nodes}, generic casts={sourceCounts.GenericCasts}, classifier blocks={sourceCounts.Blocks}; " +
                $"materialized nodes={materializedCounts.Nodes}, generic casts={materializedCounts.GenericCasts}, classifier blocks={materializedCounts.Blocks}";
            Console.WriteLine("[nested bound guard] " + report);
            var expected = depth switch { 2 => (78, 2, 4), 3 => (177, 3, 10), 4 => (334, 4, 20), _ => throw new InvalidOperationException() };
            if (!source.StructurallyEquals(materialized) || sourceCounts != materializedCounts || sourceCounts != expected)
                differences.Add(report);
        }
        if (differences.Count != 0)
            throw new InvalidOperationException("Source and actual materialized nested bound guards diverged: " + string.Join("; ", differences));

        static (int Nodes, int GenericCasts, int Blocks) Count(BirGraphValue node)
        {
            var nodes = 0;
            var casts = 0;
            var blocks = 0;
            node.Visit(current => {
                if (current is BirGraphNode) nodes++;
                if (current is BirCast { Type.Value: TypeNode.Tv }) casts++;
                if (current is BirValueBlock) blocks++;
            });
            return (nodes, casts, blocks);
        }
    }

    static void SourceIndexSelfTest()
    {
        var sourceParameter = new BirTypeParameter { Name = "T", Variance = "out" };
        var marker = new BirAttribute { Attr = new TypeNode.Fqn(MemberAttribute), Args = new IBirAttributeValue[] {
            new BirConstant { Value = new BirLiteral.TextLiteral("Helpers.original") },
            new BirAttributeLiteral { Value = new BirLiteral.TextLiteral("Slots.original") },
        } };
        var method = new BirFunctionDeclaration {
            Name = "original", DeclarationId = "source-value-method", Generated = true,
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String"), Attrs = new[] { marker },
        };
        var owner = new BirClassDefinition { Name = "SourceShape", Kind = "class",
            TypeParams = new[] { sourceParameter }, CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } },
            Methods = new[] { method } };
        var root = new BirFileDeclaration { FileClass = "SourceValues", Types = new IBirTypeDeclaration[] {
            owner,
            new BirClassDefinition { Name = "AbsentShape", Kind = "class" },
            new BirClassDefinition { Name = "NullShape", Kind = "class", TypeParams = OptionalField<BirTypeParameter[]>.Null },
            new BirClassDefinition { Name = "EmptyShape", Kind = "class", TypeParams = Array.Empty<BirTypeParameter>() },
        } };
        var membership = new SourceModuleIndex(new[] { root }, new Dictionary<string, string>()).DeclarationIds;
        var policy = new ValueRepresentationSourcePolicy(new[] { root }, null, false);
        var index = CaptureSource(new[] { root }, membership);
        var shapes = index.Parameters.ToDictionary(shape => shape.Owner, shape => shape.Variances, StringComparer.Ordinal);
        if (!shapes["SourceShape"].Value.SequenceEqual(new[] { "out" })
            || shapes["AbsentShape"].IsPresent || !shapes["NullShape"].IsNull
            || !shapes["EmptyShape"].HasValue || shapes["EmptyShape"].Value.Length != 0)
            throw new InvalidOperationException("Value source index lost declared parameter presence or concatenated captured parameters");

        // Model the continuing physical declaration rather than recapturing it:
        // generic expansion and attribute consumption do not change source facts.
        sourceParameter.Variance = "in";
        owner.TypeParams = new[] { sourceParameter, new BirTypeParameter { Name = "$storage0" } };
        method.Attrs = Array.Empty<BirAttribute>();
        var indexed = CreateSource(new[] { root }, null, false, policy, index, null);
        method.Ret = new TypeNode.Fqn("kotlin.Long");
        if (!indexed.locals["source-value-method"].Method.HasSource
            || indexed.locals["source-value-method"].Method.Return != new TypeNode.Fqn("kotlin.Long"))
            throw new InvalidOperationException("Value representation source association copied a stale method header");
        owner.Name = "PhysicalShape";
        method.Name = "physicalEntry";
        var declarations = FixtureProjection(root);
        declarations.SealSourceContinuations();
        indexed.CompleteSourceContinuation(new[] { root }, null, declarations);
        method.Ret = new TypeNode.Fqn("kotlin.Int");
        if (!indexed.localParameters["SourceShape"].SequenceEqual(new[] { "out" })
            || indexed.localParameters.ContainsKey("PhysicalShape") || indexed.localParameters.ContainsKey("AbsentShape")
            || indexed.localParameters.ContainsKey("NullShape") || indexed.localParameters["EmptyShape"].Count != 0
            || indexed.locals["source-value-method"].Owner != "SourceShape"
            || !ReferenceEquals(indexed.locals["source-value-method"].Method.SourceOccurrence, method)
            || indexed.adapters["source-value-method"] != "Helpers.original"
            || indexed.members["source-value-method"].Slot != "Slots.original"
            || !ReferenceEquals(indexed.members["source-value-method"].Method.SourceOccurrence, method)
            || !indexed.locals["source-value-method"].Method.HasSource
            || indexed.locals["source-value-method"].Method.Return != new TypeNode.Fqn("kotlin.Int"))
            throw new InvalidOperationException("Value representation recaptured expanded source facts or detached its continuing physical method");
        declarations.Release();
        Console.WriteLine("[value source index] self-test OK (parameter presence, initial shape, captured metadata, exact live identity)");
        SourceRetirementSelfTest();
    }

    static void SourceRetirementSelfTest()
    {
        BirFunctionDeclaration Method(string name) => new() { Name = name, DeclarationId = "shared-source-id",
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String") };
        var live = Method("live");
        var retired = Method("retired");
        var retiredOwner = new BirClassDefinition { Kind = "class", Name = "RetiredOwner", Methods = new[] { retired } };
        var liveOwner = new BirClassDefinition { Kind = "class", Name = "LiveOwner", Methods = new[] { live } };
        var root = new BirFileDeclaration { FileClass = "Retirement", Types = new IBirTypeDeclaration[] { retiredOwner, liveOwner } };
        var sourcePolicy = new ValueRepresentationSourcePolicy(new[] { root }, null, false);
        var values = CreateSource(new[] { root }, null, false, sourcePolicy, CaptureSource(new[] { root }), null);
        var retiredView = values.sourceMethods[0].Method;
        var liveView = values.sourceMethods[1].Method;
        var declarations = FixtureProjection(root);
        values.RetireSourceDeclaration(retired);
        declarations.RetireForest(retired);
        // A newly generated helper is not a source-catalog occurrence, even if it shares scalar header facts.
        values.RetireSourceDeclaration(Method("untracked"));
        retired.Ret = new TypeNode.Fqn("kotlin.Long");
        retiredOwner.Methods = Array.Empty<BirFunctionDeclaration>();
        declarations.SealSourceContinuations();
        values.CompleteSourceContinuation(new[] { root }, null, declarations);
        if (!retiredView.HasSource || !liveView.HasSource || values.sourceMethods.Count != 0
            || !ReferenceEquals(liveView.SourceOccurrence, live)
            || !ReferenceEquals(retiredView.SourceOccurrence, retired)
            || retiredView.Return != new TypeNode.Fqn("kotlin.Long")
            || !ReferenceEquals(values.locals["shared-source-id"].Method, liveView))
            throw new InvalidOperationException("Explicit value declaration retirement lost actual occurrence order or continuing source identity");
        retired.Ret = new TypeNode.Fqn("kotlin.Int");
        if (retiredView.Return != new TypeNode.Fqn("kotlin.Int"))
            throw new InvalidOperationException("Retired value declaration became a frozen source header after completion");
        declarations.Release();

        var missingOwner = new BirClassDefinition { Kind = "class", Name = "MissingOwner", Methods = new[] { Method("first") } };
        var missingRoot = new BirFileDeclaration { FileClass = "Missing", Types = new IBirTypeDeclaration[] {
            missingOwner, new BirClassDefinition { Kind = "class", Name = "OtherOwner", Methods = new[] { Method("second") } },
        } };
        var missing = CreateSource(new[] { missingRoot }, null, false,
            new ValueRepresentationSourcePolicy(new[] { missingRoot }, null, false), CaptureSource(new[] { missingRoot }), null);
        var missingDeclarations = FixtureProjection(missingRoot);
        missingOwner.Methods = Array.Empty<BirFunctionDeclaration>();
        var refused = false;
        try
        {
            missingDeclarations.SealSourceContinuations();
            missing.CompleteSourceContinuation(new[] { missingRoot }, null, missingDeclarations);
        }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("An unreported missing source occurrence bypassed strict value declaration handoff");
    }
}
