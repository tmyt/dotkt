using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void ProjectionContextSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Carrier projection context: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Source carrier projection accepted a bound source clock");
        }
        static bool Same(TypeNode left, TypeNode right) => TypeNodeDataEquality.Equal(left, right);
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        var any = new TypeNode.Fqn("System.Object");
        TypeNode.Fqn Use(string name, params TypeNode[] arguments) => new(name, arguments);
        BirTypeParameter Parameter(string name, string variance = null) => new() {
            Name = name, Variance = variance == null ? default : variance,
        };
        var ownerFrame = new NullableRepresentationFrame(1, new[] { 0 });
        var otherFrame = new NullableRepresentationFrame(2, Array.Empty<int>());
        var owned = new BirClassDefinition { Name = "Owned", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("T") } };
        var frameOwner = new BirClassDefinition { Name = "FrameOwner", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("T", "out"), Parameter("Nullable") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame) };
        var laterPhysicalOccurrence = new BirClassDefinition { Name = "FrameOwner", Kind = "class", Generated = true,
            TypeParams = new[] { Parameter("A"), Parameter("B") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(otherFrame) };
        var earlierRetired = new BirClassDefinition { Name = "RetiredList", Kind = "class",
            TypeParams = new[] { Parameter("T", "out"), Parameter("Nullable") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame) };
        var lastRetired = new BirClassDefinition { Name = "RetiredList", Kind = "class",
            TypeParams = new[] { Parameter("T"), Parameter("Nullable") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame) };
        var sameNameRetired = new BirClassDefinition { Name = "FrameOwner", Kind = "class",
            TypeParams = new[] { Parameter("A"), Parameter("B") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(otherFrame) };
        const string referenceFace = "System.Collections.Generic.IEnumerable";
        var blocksReferenceVariance = new BirInterfaceDefinition { Name = referenceFace, Kind = "interface",
            TypeParams = new[] { Parameter("T") } };
        var original = new IBirTypeDeclaration[] { owned, frameOwner, laterPhysicalOccurrence,
            earlierRetired, lastRetired, sameNameRetired, blocksReferenceVariance };
        var file = new BirFileDeclaration { FileClass = "ProjectionTests", Types = original };
        var aliases = new Dictionary<string, string>(refs.Aliases, StringComparer.Ordinal) {
            ["RetiredList"] = "System.Collections.Generic.List`1",
        };
        var values = GenericRepresentationPolicy.CreateSource(new[] { file }, aliases, refs);
        values.PrepareDomLocations(new[] { file });
        var ledger = new AliasDeclarationTransitions();
        file.Types = new IBirTypeDeclaration[] { owned, frameOwner, laterPhysicalOccurrence };
        // Replacement-list order is not Original declaration order.
        ledger.Record(file, original, file.Types.Value, new[] {
            new AliasDeclarationTransitions.Replacement(blocksReferenceVariance, null, null),
            new AliasDeclarationTransitions.Replacement(sameNameRetired, null, null),
            new AliasDeclarationTransitions.Replacement(lastRetired, null, null),
            new AliasDeclarationTransitions.Replacement(earlierRetired, null, null),
        });
        var bindings = PrepareCurrentDeclarations(new[] { file }, values, out var preparation);
        var proofs = PrepareNativeProofs(bindings, refs, values);
        var demand = PrepareCarrierDemand(preparation, proofs, refs, values);
        var source = CarrierProjectionContext.ForSource(demand, refs, ledger);
        var absentLogical = CarrierProjectionContext.ForSource(demand, refs);
        var currentApplication = Use("FrameOwner", text, text);
        var retiredApplication = Use("RetiredList", text);
        var foreignApplication = Use(referenceFace, text);

        Require(source.ProjectionCanChangeConstruction(currentApplication)
            && ReferenceEquals(source.OwnerFrame(demand.Owners["FrameOwner"]), ownerFrame)
            && !source.RequiresOpaqueVariantAlias(retiredApplication),
            "current first occurrence, owner-frame role or last-Original alias header was replaced");
        Require(absentLogical.ProjectionCanChangeConstruction(foreignApplication)
            && !source.ProjectionCanChangeConstruction(foreignApplication)
            && !source.TryExistentialCarrier(referenceFace, out _)
            && source.TryExistentialCarrier("Owned", out var carrier) && carrier == "Owned" + CarrierMark
            && !source.TryExistentialCarrier("RetiredList", out _),
            "explicit local facts did not block reference variance or an unallocated local acquired a carrier");

        // Read through the current binding and the retired record, not frozen
        // facts captured by an earlier context or the last current occurrence.
        frameOwner.TypeParams.Value[0].Variance = default;
        Require(!source.ProjectionCanChangeConstruction(currentApplication), "current variance facts were cached before their actual mutation");
        frameOwner.TypeParams.Value[0].Variance = "out";
        lastRetired.TypeParams.Value[0].Variance = "out";
        Require(source.RequiresOpaqueVariantAlias(retiredApplication), "retired alias params/frame were replaced by absent referenced facts");
        lastRetired.TypeParams.Value[0].Variance = default;

        var logical = Use("Owned", text);
        var expectedCarrier = new TypeNode.Fqn("Owned" + CarrierMark);
        var methodVariable = new TypeNode.Tv("method", 0);
        var cases = new (TypeNode Input, TypeNode Expected, bool Bound, bool PreserveHead, bool PreserveArray)[] {
            (logical, expectedCarrier, false, false, false),
            (new TypeNode.Nullable(logical), new TypeNode.Nullable(expectedCarrier), false, false, false),
            (new TypeNode.Oblivious(logical), new TypeNode.Oblivious(expectedCarrier), false, false, false),
            (logical, logical, true, false, false),
            (logical, logical, false, true, false),
            (new TypeNode.ByRef(logical), new TypeNode.ByRef(logical), false, false, false),
            (new TypeNode.Array(methodVariable), new TypeNode.Fqn("System.Array"), false, false, false),
            (new TypeNode.Array(methodVariable), new TypeNode.Array(methodVariable), false, false, true),
            (new TypeNode.Projection("out", logical), expectedCarrier, false, false, false),
            (new TypeNode.Fn(false, logical, new TypeNode[] { new TypeNode.Nullable(logical) }),
                new TypeNode.Fn(false, expectedCarrier, new TypeNode[] { new TypeNode.Nullable(expectedCarrier) }), false, false, false),
        };
        var before = (BirFileDeclaration)file.DeepClone();
        var sourceResults = cases.Select(sample => source.Rewrite(sample.Input, sample.Bound, sample.PreserveHead, sample.PreserveArray)).ToArray();
        Require(cases.Select((sample, index) => Same(sourceResults[index], sample.Expected)).All(value => value)
            && Same(source.ProjectOrdinaryParameter(new TypeNode.Nullable(logical)), new TypeNode.Nullable(expectedCarrier))
            && file.StructurallyEquals(before), "shared source projection changed an expected shape, wrapper, native witness or owning declaration");

        // The actual one-way projection supplies addresses. This is the DOM
        // adapter to the SAME scalar projection body, not a historical oracle.
        var physical = file.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        values.BindDomDeclarations(new JsonNode[] { physical }, ledger);
        Require(KotlinSupertypesRecord.ReadNullableFrame(values.LocalDeclaration("FrameOwner"))?.SourceArity == 2,
            "pre-registration logical last-Original clock was not exercised");
        values.RegisterLocalDefinitions(preparation.BindDefinitions(), preparation.LocalClrAliases);
        var dom = CarrierProjectionContext.ForDom(demand.Owners, refs, values, proofs, preparation.LocalClrAliases);
        Require(dom.ProjectionCanChangeConstruction(currentApplication)
            && dom.OwnerFrame(demand.Owners["FrameOwner"]).SourceArity == 1
            && dom.OwnerFrame(demand.Owners["FrameOwner"]).NullableIndices.SequenceEqual(new[] { 0 })
            && !dom.RequiresOpaqueVariantAlias(retiredApplication)
            && !dom.ProjectionCanChangeConstruction(foreignApplication),
            "DOM registration lost current-first, logical-last, owner-frame or reference-blocking policy");
        foreach (var (sample, index) in cases.Select((sample, index) => (sample, index)))
            Require(Same(dom.Rewrite(sample.Input, sample.Bound, sample.PreserveHead, sample.PreserveArray), sourceResults[index]),
                "typed and continuing DOM adapters selected different scalar projection for case " + index);
        Require(Same(dom.ProjectOrdinaryParameter(new TypeNode.Nullable(logical)), new TypeNode.Nullable(expectedCarrier)),
            "continuing ordinary parameter disagreed with source projection");
        Reject(() => source.Rewrite(any));
        Reject(() => source.ProjectOrdinaryParameter(any));
        Reject(() => source.IsCapturedInner(logical));
        ledger.Release();
        Console.WriteLine("[carrier projection context] self-test OK (shared policies, first/current and last/logical facts, frames, reference variance and one-way clock)");
    }
}
