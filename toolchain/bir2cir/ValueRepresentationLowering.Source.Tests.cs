using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    // Reuse SelfTest's actual Union binding and the same saved-bound frame on both real entry points.
    // Prepare the typed cases before the one-way declaration handoff; run the detached materialized cases after it.
    static Action PrepareNestedBoundGuardSelfTest(ValueRepresentationLowering policy)
    {
        var cases = new List<(int Depth, BirFunctionDeclaration Source, JsonObject Materialized)>();
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
            var materialized = method.ToDom();
            policy.BindOperations(new BirFileDeclaration { Methods = new[] { method } });
            cases.Add((depth, method, materialized));
        }
        return () => {
            var differences = new List<string>();
            foreach (var (depth, source, materialized) in cases)
            {
                policy.BindMaterializedOperations(materialized);
                var typed = source.ToDom();
                var sourceCounts = Count(typed);
                var materializedCounts = Count(materialized);
                var report = $"depth={depth}: typed nodes={sourceCounts.Nodes}, generic casts={sourceCounts.GenericCasts}, classifier blocks={sourceCounts.Blocks}; " +
                    $"materialized nodes={materializedCounts.Nodes}, generic casts={materializedCounts.GenericCasts}, classifier blocks={materializedCounts.Blocks}";
                Console.WriteLine("[nested bound guard] " + report);
                if (!JsonNode.DeepEquals(typed, materialized)
                    || sourceCounts != materializedCounts || sourceCounts.GenericCasts != depth)
                    differences.Add(report);
            }
            if (differences.Count != 0)
                throw new InvalidOperationException("Source and actual materialized nested bound guards diverged: " + string.Join("; ", differences));
        };

        static (int Nodes, int GenericCasts, int Blocks) Count(JsonNode node)
        {
            var nodes = 0;
            var casts = 0;
            var blocks = 0;
            void Walk(JsonNode current)
            {
                if (current is JsonObject obj)
                {
                    var kind = Text(obj["k"]);
                    if (kind != null) nodes++;
                    if (kind == "cast" && TypeJson.Read(obj["type"]) is TypeNode.Tv) casts++;
                    if (kind == "valueBlock") blocks++;
                    foreach (var child in obj) Walk(child.Value);
                }
                else if (current is JsonArray array) foreach (var child in array) Walk(child);
            }
            Walk(node);
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
        var continuation = root.ToDom();
        var physicalOwner = continuation["types"][0].AsObject();
        physicalOwner["name"] = "PhysicalShape";
        var physicalMethod = physicalOwner["methods"][0].AsObject();
        physicalMethod["name"] = "physicalEntry";
        indexed.BindDomDeclarations(new JsonNode[] { continuation });
        physicalMethod["ret"] = TypeJson.Fqn("kotlin.Int");
        if (!indexed.localParameters["SourceShape"].SequenceEqual(new[] { "out" })
            || indexed.localParameters.ContainsKey("PhysicalShape") || indexed.localParameters.ContainsKey("AbsentShape")
            || indexed.localParameters.ContainsKey("NullShape") || indexed.localParameters["EmptyShape"].Count != 0
            || indexed.locals["source-value-method"].Owner != "SourceShape"
            || !ReferenceEquals(indexed.locals["source-value-method"].Method.Dom, physicalMethod)
            || indexed.adapters["source-value-method"] != "Helpers.original"
            || indexed.members["source-value-method"].Slot != "Slots.original"
            || !ReferenceEquals(indexed.members["source-value-method"].Method.Dom, physicalMethod)
            || indexed.locals["source-value-method"].Method.HasSource
            || indexed.locals["source-value-method"].Method.Return != new TypeNode.Fqn("kotlin.Int"))
            throw new InvalidOperationException("Value representation recaptured expanded source facts or detached its continuing physical method");
        Console.WriteLine("[value source index] self-test OK (parameter presence, initial shape, captured metadata, exact live identity)");
        SourceRetirementSelfTest();
    }

    static void SourceRetirementSelfTest()
    {
        BirFunctionDeclaration Method(string name) => new() { Name = name, DeclarationId = "shared-source-id",
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.String") };
        var live = Method("live");
        var retired = Method("retired");
        var root = new BirFileDeclaration { FileClass = "Retirement", Methods = new[] { retired, live } };
        var sourcePolicy = new ValueRepresentationSourcePolicy(new[] { root }, null, false);
        var values = CreateSource(new[] { root }, null, false, sourcePolicy, CaptureSource(new[] { root }), null);
        var retiredView = values.sourceMethods[0].Method;
        var liveView = values.sourceMethods[1].Method;
        values.RetireSourceDeclaration(retired);
        // A newly generated helper is not a source-catalog occurrence, even if it shares scalar header facts.
        values.RetireSourceDeclaration(Method("untracked"));
        retired.Ret = new TypeNode.Fqn("kotlin.Long");
        root.Methods = new[] { live };
        var continuing = root.ToDom();
        var retiredProjection = retired.ToDom();
        var retirements = new Dictionary<BirFunctionDeclaration, JsonObject>(ReferenceEqualityComparer.Instance) { [retired] = retiredProjection };
        values.BindDomDeclarations(new JsonNode[] { continuing }, retirements);
        if (retiredView.HasSource || liveView.HasSource || values.sourceMethods.Count != 0
            || !ReferenceEquals(liveView.Dom, continuing["methods"][0])
            || !ReferenceEquals(retiredView.Dom, retiredProjection)
            || retiredView.Return != new TypeNode.Fqn("kotlin.Long")
            || !ReferenceEquals(values.locals["shared-source-id"].Method, liveView))
            throw new InvalidOperationException("Explicit value declaration retirement lost actual occurrence order or retained source references");
        retiredView.Dom["ret"] = TypeJson.Fqn("kotlin.Int");
        if (retiredView.Return != new TypeNode.Fqn("kotlin.Int"))
            throw new InvalidOperationException("Retired value declaration became a frozen source header after handoff");

        var missingRoot = new BirFileDeclaration { FileClass = "Missing", Methods = new[] { Method("first"), Method("second") } };
        var missing = CreateSource(new[] { missingRoot }, null, false,
            new ValueRepresentationSourcePolicy(new[] { missingRoot }, null, false), CaptureSource(new[] { missingRoot }), null);
        missingRoot.Methods = new[] { missingRoot.Methods.Value[1] };
        var refused = false;
        try { missing.BindDomDeclarations(new JsonNode[] { missingRoot.ToDom() }); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("An unreported missing source occurrence bypassed strict value declaration handoff");
    }
}
