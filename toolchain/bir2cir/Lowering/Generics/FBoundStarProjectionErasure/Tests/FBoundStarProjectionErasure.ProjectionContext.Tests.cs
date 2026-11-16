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
            Base = new TypeNode.Fqn("kotlin.Any"),
            TypeParams = new[] { Parameter("T") } };
        var frameOwner = new BirClassDefinition { Name = "FrameOwner", Kind = "class", Vis = "public",
            Base = new TypeNode.Fqn("kotlin.Any"),
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
            ["kotlin.Any"] = "System.Object",
        };
        var values = GenericRepresentationPolicy.CreateSource(new[] { file }, aliases, refs);
        values.PrepareSourceLocations(new[] { file });
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

        // Physical type lowering keeps the original scalar hierarchy separate
        // from live current headers and explicit retired logical facts.
        var originalHierarchy = bindings.SourceSupertypes["Owned"].Base;
        BirTypeLowering.Apply(file, new BirTypeLowering.PhysicalTypeContext(false, aliases));
        Require(frameOwner.Base.Value is TypeNode.Fqn { Name: "System.Object" }
            && originalHierarchy is { Name: "kotlin.Any" }
            && ReferenceEquals(bindings.SourceSupertypes["Owned"].Base, originalHierarchy)
            && source.ProjectionCanChangeConstruction(currentApplication)
            && !source.RequiresOpaqueVariantAlias(retiredApplication)
            && ReferenceEquals(source.OwnerFrame(demand.Owners["FrameOwner"]), ownerFrame),
            "physical types changed captured hierarchy or the live projection header's variance/frame policy");
        var custody = new SourceDeclarationProjection(new[] { file }, bindings);
        custody.SealSourceContinuations();
        values.CompleteSourceContinuation(new[] { file }, ledger, custody);
        Require(ledger.TryRetiredLogicalHeader("FrameOwner", out var retiredHeader)
            && ReferenceEquals(retiredHeader.KotlinSupertypesPre.Value.NullableFrame, otherFrame)
            && ReferenceEquals(bindings.SourceDefinitions["FrameOwner"].Source, frameOwner),
            "retired last-Original and current first-occurrence facts were conflated");
        var continuing = CarrierProjectionContext.ForSource(demand, refs, ledger);
        Require(continuing.ProjectionCanChangeConstruction(currentApplication)
            && continuing.OwnerFrame(demand.Owners["FrameOwner"]).SourceArity == 1
            && continuing.OwnerFrame(demand.Owners["FrameOwner"]).NullableIndices.SequenceEqual(new[] { 0 })
            && !continuing.RequiresOpaqueVariantAlias(retiredApplication)
            && !continuing.ProjectionCanChangeConstruction(foreignApplication),
            "typed continuation lost current-first, logical-last, owner-frame or reference-blocking policy");
        foreach (var sample in cases)
            Require(Same(continuing.Rewrite(sample.Input, sample.Bound, sample.PreserveHead, sample.PreserveArray), sample.Expected),
                "live typed continuation changed the normative scalar projection");
        Require(Same(continuing.ProjectOrdinaryParameter(new TypeNode.Nullable(logical)), new TypeNode.Nullable(expectedCarrier)),
            "continuing ordinary parameter disagreed with its expected source policy");
        demand.CompleteSourceLifetime();
        bindings.CompleteSourceCustody(custody);
        Reject(() => source.Rewrite(any));
        Reject(() => source.ProjectOrdinaryParameter(any));
        Reject(() => source.IsCapturedInner(logical));
        ledger.Release();
        custody.Release();
        Console.WriteLine("[carrier projection context] self-test OK (shared policies, first/current and last/logical facts, frames, reference variance and one-way clock)");
    }
}
