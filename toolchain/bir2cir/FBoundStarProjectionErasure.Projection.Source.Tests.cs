using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static void SourceProjectionContractSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning projection facts: " + message); }
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        BirTypeParameter Parameter(string name, params TypeNode[] bounds) => new() {
            Name = name, Constraints = bounds.Length == 0 ? default : bounds,
        };
        TypeNode Bound(TypeNode argument) => new TypeNode.Fqn("Bound", new[] { argument });
        var tag = new TypeNode.Tv("type", 0);
        var dependent = new BirClassDefinition { Name = "Dependent", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("Tag"), Parameter("Self", Bound(new TypeNode.Tv("type", 1))),
                Parameter("T", Bound(tag)) } };
        var method = new BirFunctionDeclaration { Name = "dependent", Params = Array.Empty<BirParameter>(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>(),
            TypeParams = new[] { Parameter("Tag"), Parameter("T", Bound(new TypeNode.Tv("method", 0))) } };
        var materialized = new BirClassDefinition { Name = "Materialized", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("T"), Parameter("U", Bound(new TypeNode.Tv("type", 2))), Parameter("N") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(new NullableRepresentationFrame(2, new[] { 0 })) };
        var materializedMethod = new BirFunctionDeclaration { Name = "materialized", Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { Parameter("T"), Parameter("U", Bound(new TypeNode.Tv("method", 0))), Parameter("N") },
            NullableRepresentationFrame = new NullableRepresentationFrame(2, new[] { 0 }),
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [1] = Array.Empty<TypeNode>() }) };
        var inner = new BirClassDefinition { Name = "Owner.Inner", Kind = "class", Vis = "public",
            Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
            TypeParams = new[] { Parameter("T"), Parameter("E", tag), Parameter("storageE", Bound(new TypeNode.Tv("type", 1))) },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(new NullableRepresentationFrame(2,
                Array.Empty<int>(), storageIndices: new[] { 1 })) };
        var projectedInner = new BirClassDefinition { Name = "Owner.Projected", Kind = "class", Vis = "public",
            Mods = new BirModifiers { Inner = true }, OuterTypeParamCount = 1,
            TypeParams = new[] { Parameter("T"), Parameter("E", Bound(tag), new TypeNode.Fqn("Marker")) } };
        var constructionFrame = new NullableRepresentationFrame(2, Array.Empty<int>(),
            new[] { 2, 0, 3, 1 }, storageIndices: new[] { 0, 1 });
        var constructionOwner = new BirClassDefinition { Name = "Construction", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("P"), Parameter("T"), Parameter("Q"), Parameter("U") },
            KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(constructionFrame) };
        var file = new BirFileDeclaration { FileClass = "ProjectionFacts", Methods = new[] { method, materializedMethod },
            Types = new IBirTypeDeclaration[] { new BirInterfaceDefinition { Name = "Bound", Kind = "interface", Vis = "public",
                TypeParams = new[] { Parameter("T") } }, dependent, materialized, inner, projectedInner, constructionOwner } };
        try
        {
            var context = SourceSynthesisTestContext(file, refs);
            foreach (var owner in context.Owners.Values) owner.Needed = true;
            var state = new SourceRewriteState(context, (_, _) => null);
            var innerArgument = Bound(new TypeNode.Tv("method", 0));
            var construction = new TypeNode.Fqn(constructionOwner.Name.Value, Enumerable.Repeat<TypeNode>(innerArgument, 4).ToArray());
            var companion = construction with { Args = construction.Args.Select((argument, index) =>
                constructionFrame.PhysicalSlot(index).Representation == NullableRepresentationFrame.Role.Storage
                    ? (TypeNode)new TypeNode.Projection("out", argument) : argument).ToArray() };
            var hidden = companion with { Args = companion.Args.Select((argument, index) =>
                index == constructionFrame.SourcePosition(0) ? new TypeNode.Projection("out", innerArgument) : argument).ToArray() };
            var nominal = construction with { Args = construction.Args.Select((argument, index) =>
                index == 1 ? Bound(new TypeNode.Star()) : argument).ToArray() };
            var foreign = construction with { Args = construction.Args.Select((argument, index) => index == 1
                ? new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { new TypeNode.Star() }) : argument).ToArray() };
            Require(!ConstructionHasHiddenWitnessSource(companion, state) && ConstructionHasHiddenWitnessSource(hidden, state)
                && !ConstructionHasHiddenWitnessSource(nominal, state) && ConstructionHasHiddenWitnessSource(foreign, state),
                "constructor witnesses confused storage companions, a hidden source slot, nominal carriers or foreign frames");
            RecordDeclarationSurfacesSource(file, context);
            var typeBounds = dependent.KotlinSupertypesPre.Value.Bounds[2].ToArray();
            var methodBounds = method.MethodTypeParameterBoundsPre.Value.Bounds[1].ToArray();
            var slot = InterfaceSlotSource(method, "slot", "ProjectionFacts", context);
            ProjectDependentTypeConstraintsSource(dependent, context);
            TypeNode Project(TypeNode bound) => ProjectOwnerMethodBound(bound, context.Projection, physical: false);
            TypeNode Classifier(TypeNode bound) => PhysicalBoundClassifier(bound, context.Projection);
            OwnerConstrainedMethodLowering.CloseOwnersSource(context, ContainsOwnerTv, Project, Classifier);
            OwnerConstrainedMethodLowering.ApplySource(context, ContainsOwnerTv,
                bound => ProjectOwnerMethodBound(bound, context.Projection), Classifier);
            var expected = new TypeNode.Fqn(context.Owners["Bound"].ErasedName);
            Require(dependent.TypeParams.Value[2].Constraints.Value.Single() == expected
                && dependent.TypeParams.Value[1].Constraints.Value.Single() == Bound(new TypeNode.Tv("type", 1))
                && dependent.KotlinSupertypesPre.Value.Bounds[2].SequenceEqual(typeBounds)
                && PhysicalBoundClassifier(new TypeNode.Fqn("Native", new[] { tag }), context.Projection) == null,
                "dependent classifier, self-bound or recorded source type bounds changed");
            Require(method.TypeParams.Value[1].Constraints.Value.Single() == expected
                && slot.TypeParams.Value[1].Constraints.Value.Single() == expected
                && method.MethodTypeParameterBoundsPre.Value.Bounds[1].SequenceEqual(methodBounds),
                "source method and authored slot disagreed on the classifier or replaced original bounds");
            Require(materialized.KotlinSupertypesPre.Value.Bounds.Count == 0
                && materializedMethod.MethodTypeParameterBoundsPre.Value.Bounds[1].Length == 0,
                "physical companion constraints entered Kotlin type/method source facts");
            var dependencies = CaptureInnerConstraintDependenciesSource(context);
            projectedInner.TypeParams.Value[1].Constraints.Value[0] = new TypeNode.Fqn("kotlin.Any");
            var changed = WeakenOwnerDependentInnerConstraintsSource(context, dependencies);
            Require(changed.ContainsKey(inner.Name.Value) && inner.KotlinSupertypesPre.Value.Bounds.Count == 0
                && projectedInner.TypeParams.Value[1].Constraints.Value.Length == 0,
                "physical inner source facts changed or value projection resurrected a bottom-fixed constraint");
        }
        finally { OwnerConstrainedMethodLowering.Reset(refs); }
    }

    static void LocalAliasDeclarationSelfTest()
    {
        var savedPolicy = _valueSlots;
        var savedProofs = _nativeProofs;
        var savedGraph = _sourceSupertypes;
        var savedMethods = _localMethods;
        var savedDeclarations = _localDeclarations;
        var savedNames = _usedCarrierNames;
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        try
        {
            var text = new TypeNode.Fqn("kotlin.String");
            TypeNode Alias(TypeNode argument) => new TypeNode.Fqn("SourceAlias", new[] { argument });
            var sourceType = Alias(new TypeNode.Tv("method", 0));
            var parameter = new BirParameter { Name = "value", Type = sourceType };
            var field = new BirFieldDeclaration { Name = "field", Type = Alias(text) };
            var nestedType = new TypeNode.Fn(false, Alias(text), Array.Empty<TypeNode>(), null);
            var nested = new BirParameter { Name = "value", Type = nestedType };
            var method = new BirFunctionDeclaration { Name = "identity", Params = new[] { parameter }, Ret = sourceType,
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = Array.Empty<IBirStatement>() };
            var file = new BirFileDeclaration { FileClass = "AliasDeclarationProbe", Methods = new[] { method,
                new BirFunctionDeclaration { Name = "nested", Params = new[] { nested }, Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() } },
                Types = new IBirTypeDeclaration[] {
                    new BirInterfaceDefinition { Name = "SourceAlias", Kind = "interface",
                        TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
                        Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"), Args = new IBirAttributeValue[] {
                            new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("System.Collections.Generic.IReadOnlyCollection") } } } } },
                    new BirClassDefinition { Name = "CapturedOnly", Kind = "class", Mods = new BirModifiers { Inner = true },
                        OuterTypeParamCount = 1, TypeParams = new[] { new BirTypeParameter { Name = "Outer" } } },
                    new BirClassDefinition { Name = "GeneratedCaptured", Kind = "class", Generated = true, Mods = new BirModifiers { Inner = true },
                        OuterTypeParamCount = 1, TypeParams = new[] { new BirTypeParameter { Name = "Outer" } } },
                    new BirClassDefinition { Name = "AliasHolder", Kind = "class", Fields = new[] { field } },
                } };
            var context = SourceSynthesisTestContext(file, refs);
            context.Values.PrepareDomLocations(new[] { file });
            var projected = context.Projection.Rewrite(sourceType);
            var captured = new TypeNode.Fqn("CapturedOnly", new TypeNode[] { text });
            Require(projected == new TypeNode.Fqn("kotlin.Any") && !context.Owners.ContainsKey("SourceAlias")
                && context.Projection.ProjectionCanChangeConstruction((TypeNode.Fqn)sourceType)
                && context.Projection.IsCapturedInner(captured) && context.Projection.RequiresKotlinValueCarrier(captured)
                && !context.Projection.RequiresKotlinValueCarrier(captured with { Name = "GeneratedCaptured" })
                && !context.Owners.ContainsKey("GeneratedCaptured"), "local alias or captured-only declaration eligibility changed");
            RecordDeclarationSurfacesSource(file, context);
            Require(parameter.KotlinType.Value == sourceType && method.RetKotlinType.Value == sourceType
                && field.KotlinType.Value == Alias(text) && nested.KotlinType.Value == nestedType,
                "alias parameter/return/field/nested source metadata was not captured before projection");
            RewriteCarrierBodiesSource(new[] { file }, context, Array.Empty<SharedSyntheticSynthesis.CellStorage>(), (_, _) => null);
            Require(parameter.Type.Value == projected && method.Ret.Value == projected && field.Type.Value == projected,
                "owning declaration projection disagreed with source alias value policy");
            RewriteLateTypesSource(new[] { file }, context);
            var physical = file.ToDom(context.Bindings.ObserveProjection);
            context.Bindings.BindProjected();
            context.Values.BindDomDeclarations(new System.Text.Json.Nodes.JsonNode[] { physical });
            BindRealizationContinuation(context);
            Require(TypeJson.Read(physical["methods"][0]["params"][0]["type"]) == projected
                && TypeJson.Read(physical["methods"][0]["ret"]) == projected
                && Str(physical["methods"][0]["params"][0]["kotlinType"]) == TypeNode.ToJson(sourceType)
                && Str(physical["methods"][0]["retKotlinType"]) == TypeNode.ToJson(sourceType)
                && Str(physical["types"][3]["fields"][0]["kotlinType"]) == TypeNode.ToJson(Alias(text))
                && Str(physical["methods"][1]["params"][0]["kotlinType"]) == TypeNode.ToJson(nestedType),
                "genuine late projection changed alias ABI or reconstructed source metadata");
        }
        finally
        {
            _valueSlots = savedPolicy; _nativeProofs = savedProofs; _sourceSupertypes = savedGraph;
            _localMethods = savedMethods; _localDeclarations = savedDeclarations; _usedCarrierNames = savedNames;
            OwnerConstrainedMethodLowering.Reset(refs);
        }

        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning alias declaration: " + message); }
    }
}
