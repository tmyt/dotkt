using DotKt.Bir;
using DotKt.Bir.Lowering;
using System.Text.Json.Nodes;

static partial class FBoundStarProjectionErasure
{
    internal static void LateTypesSourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Current physical carrier projection: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Late projection admitted an unreported declaration or bound source clock");
        }
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("kotlin.String");
        var native = new TypeNode.Fqn("System.IDisposable");
        BirTypeParameter Parameter(string name) => new() { Name = name };
        var generic = new BirClassDefinition { Name = "LateGeneric", Kind = "class", Vis = "public",
            TypeParams = new[] { Parameter("T") } };
        var originalMethod = new BirFunctionDeclaration { Name = "original", DeclarationId = "original",
            Params = Array.Empty<BirParameter>(), Ret = text, Body = Array.Empty<IBirStatement>() };
        var file = new BirFileDeclaration { FileClass = "LateProjectionTests", Types = new IBirTypeDeclaration[] { generic },
            Methods = new[] { originalMethod } };
        var savedPolicy = _valueSlots; var savedProofs = _nativeProofs; var savedNames = _usedCarrierNames;
        var savedGraph = _sourceSupertypes; var savedMethods = _localMethods; var savedDeclarations = _localDeclarations;
        try
        {
            var context = SourceSynthesisTestContext(file, references);
            var roots = new[] { file };
            var oldDefinitions = context.Bindings.SourceDefinitions;
            var oldDeclarations = context.Bindings.SourceLocalDeclarations;
            var star = new TypeNode.Fqn("LateGeneric", new TypeNode[] { new TypeNode.Star() });
            var carrier = new TypeNode.Fqn(context.Owners["LateGeneric"].ErasedName);
            var result = new BirFunctionDeclaration { Name = "resume", DeclarationId = "late-resume",
                Ret = star, RetKotlinType = star, Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
            var machine = new BirClassDefinition { Name = "LateMachine", Kind = "class", Generated = true,
                TypeParams = new[] { new BirTypeParameter { Name = "N", Constraints = new TypeNode[] { native } } },
                Methods = new[] { result }, Fields = new[] { new BirFieldDeclaration { Name = "value", Type = star, KotlinType = star } } };
            file.Types = file.Types.Value.Append(machine).ToArray();
            Reject(() => CurrentPhysicalTypeView.Capture(roots, context.Bindings));
            // This is the producer notification, not admission by a projection query.
            var machineBinding = context.Bindings.GetSource(machine);
            context.Bindings.GetSource(result);
            var first = CurrentPhysicalTypeView.Capture(roots, context.Bindings);
            Require(ReferenceEquals(first.Definitions["LateMachine"], machine)
                && ReferenceEquals(first.Bindings["LateMachine"], machineBinding), "current actual declaration was reconstructed");
            RewriteLateTypesSource(roots, context);
            Require(result.Ret.Value == carrier && result.RetKotlinType.Value == star
                && machine.Fields.Value[0].Type.Value == carrier && machine.Fields.Value[0].KotlinType.Value == star,
                "current body/declaration slots or frozen source signatures crossed authorities");
            Require(machine.TypeParams.Value[0].Constraints.Value[0] == native
                && context.NativeProofs.IsPhysicalConstraint(machineBinding, 0, native)
                && context.NativeProofs.ExactTypeArgument("LateMachine", 0), "reported physical type lost its exact native constraint");
            Require(ReferenceEquals(context.Bindings.SourceDefinitions, oldDefinitions)
                && !oldDefinitions.ContainsKey("LateMachine") && !oldDeclarations.ContainsKey(("LateMachine", "late-resume"))
                && context.NativeProofs.LocalSourceMethodBinding("late-resume") == null,
                "physical refresh extended original source declaration or selected-method catalogs");

            // A current first occurrence supplies variance/frame, not the early
            // declaration or a later duplicate. Neither needs new carrier eligibility.
            var frame = new NullableRepresentationFrame(1, new[] { 0 });
            var currentGeneric = new BirClassDefinition { Name = "LateGeneric", Kind = "class", Generated = true,
                TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" }, Parameter("N") },
                KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame) };
            context.Bindings.GetSource(currentGeneric);
            file.Types = new IBirTypeDeclaration[] { currentGeneric, generic, machine };
            var current = CurrentPhysicalTypeView.Capture(roots, context.Bindings);
            var projection = CarrierProjectionContext.ForCurrentSource(context, current);
            Require(ReferenceEquals(current.Definitions["LateGeneric"], currentGeneric)
                && projection.ProjectionCanChangeConstruction(new TypeNode.Fqn("LateGeneric", new TypeNode[] { text, text }))
                && ReferenceEquals(projection.OwnerFrame(projection.Owners["LateGeneric"]), frame)
                && !context.Projection.ProjectionCanChangeConstruction(new TypeNode.Fqn("LateGeneric", new TypeNode[] { text })),
                "current-first variance/frame lookup reused frozen early or last-duplicate facts");
            file.Types = new IBirTypeDeclaration[] { generic, machine, currentGeneric };

            // Final override synthesis occurs between the two real calls. The
            // second pass observes its exact newly attached declaration.
            var bridgeMethod = new BirFunctionDeclaration { Name = "bridge", DeclarationId = "late-bridge", Ret = star,
                Params = Array.Empty<BirParameter>(), Body = Array.Empty<IBirStatement>() };
            var bridge = new BirClassDefinition { Name = "LateBridge", Kind = "class", Generated = true,
                TypeParams = new[] { new BirTypeParameter { Name = "B", Constraints = new TypeNode[] { native } } },
                Methods = new[] { bridgeMethod } };
            file.Types = file.Types.Value.Append(bridge).ToArray();
            var bridgeBinding = context.Bindings.GetSource(bridge);
            context.Bindings.GetSource(bridgeMethod);
            var finalCurrent = RewriteLateTypesSource(roots, context);
            Require(bridgeMethod.Ret.Value == carrier && context.NativeProofs.IsPhysicalConstraint(bridgeBinding, 0, native)
                && !context.Owners.ContainsKey("LateBridge") && !context.Bindings.SourceDefinitions.ContainsKey("LateBridge"),
                "second projection missed an authored bridge or selected a new source carrier");

            // The SAME physical rows survive the one actual address handoff.
            var physical = file.ToDom(context.Bindings.ObserveProjection);
            context.Bindings.BindProjected();
            var physicalBridge = bridgeBinding.Physical;
            Require(context.NativeProofs.IsPhysicalConstraint(physicalBridge["typeParams"][0]["constraints"].AsArray(), native)
                && ReferenceEquals(physical["types"][3], physicalBridge), "physical proof continuation changed at the frontier");
            BindRealizationContinuation(context, finalCurrent);
            Require(ReferenceEquals(context.Values.LocalDeclaration("LateBridge"), physicalBridge)
                && ReferenceEquals(context.Values.LocalDeclaration("LateMachine"), machineBinding.Physical)
                && ReferenceEquals(context.Values.LocalDeclaration("LateGeneric"), context.Bindings.GetPhysical(physical["types"][0].AsObject()).Physical)
                && !oldDefinitions.ContainsKey("LateBridge") && !oldDeclarations.ContainsKey(("LateBridge", "late-bridge")),
                "current physical declaration handoff was replaced by an early/logical header or extended source catalogs");
            Reject(() => RewriteLateTypesSource(roots, context));

            var alias = new BirClassDefinition { Name = "RetainedAlias", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
                Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"), Args = new IBirAttributeValue[] {
                    new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("System.Collections.Generic.IReadOnlyCollection") } } } } };
            var aliasFile = new BirFileDeclaration { FileClass = "LateAliasTests", Types = new IBirTypeDeclaration[] { alias } };
            var aliasValues = GenericRepresentationPolicy.CreateSource(new[] { aliasFile },
                new Dictionary<string, string> { ["RetainedAlias"] = "System.Collections.Generic.IReadOnlyCollection" }, references);
            aliasValues.PrepareDomLocations(new[] { aliasFile });
            var aliasLedger = new AliasDeclarationTransitions();
            var originalAliasVector = aliasFile.Types.Value;
            aliasFile.Types = Array.Empty<IBirTypeDeclaration>();
            aliasLedger.Record(aliasFile, originalAliasVector, aliasFile.Types.Value,
                new[] { new AliasDeclarationTransitions.Replacement(alias, null, null) });
            var aliasBindings = PrepareCurrentDeclarations(new[] { aliasFile }, aliasValues, out var aliasPreparation);
            var aliasProofs = PrepareNativeProofs(aliasBindings, references, aliasValues);
            var aliasSurface = PrepareCarrierSurface(aliasPreparation, aliasProofs, references, aliasValues);
            var aliasContext = new CarrierRealizationContext(aliasSurface,
                CarrierProjectionContext.ForSource(aliasSurface.Demand, references, aliasLedger), references, aliasSurface.Demand.ReservedNames);
            var aliasCurrent = CurrentPhysicalTypeView.Capture(new[] { aliasFile }, aliasBindings);
            var aliasProjection = CarrierProjectionContext.ForCurrentSource(aliasContext, aliasCurrent, aliasLedger);
            Require(aliasCurrent.LocalClrAliases.Count == 0
                && aliasProjection.RequiresOpaqueVariantAlias(new TypeNode.Fqn("RetainedAlias", new TypeNode[] { text }))
                && !aliasProjection.TryExistentialCarrier("RetainedAlias", out _),
                "explicit retained alias header was lost or confused with a current physical alias/carrier");
            var physicalHost = new BirClassDefinition { Name = "RetainedAlias", Kind = "class", Generated = true };
            aliasFile.Types = new IBirTypeDeclaration[] { physicalHost };
            aliasLedger.AdmitMaterializedTypes(aliasFile, new[] { physicalHost });
            aliasBindings.GetSource(physicalHost);
            var aliasFinal = RewriteLateTypesSource(new[] { aliasFile }, aliasContext, aliasLedger);
            var aliasDom = aliasFile.ToDom(aliasBindings.ObserveProjection);
            aliasBindings.BindProjected();
            aliasValues.BindDomDeclarations(new JsonNode[] { aliasDom }, aliasLedger);
            Require(aliasValues.LocalDeclaration("RetainedAlias")["typeParams"].AsArray().Count == 1
                && !ReferenceEquals(aliasValues.LocalDeclaration("RetainedAlias"), aliasDom["types"][0]),
                "retained logical alias header clock was not exercised before the physical continuation");
            BindRealizationContinuation(aliasContext, aliasFinal);
            Require(ReferenceEquals(aliasValues.LocalDeclaration("RetainedAlias"), aliasDom["types"][0])
                && !aliasValues.LocalDeclaration("RetainedAlias").ContainsKey("typeParams")
                && aliasBindings.SourceDefinitions.Count == 0,
                "late physical alias host was shadowed by its retained logical header or admitted as a source owner");
            aliasLedger.Release();
        }
        finally
        {
            _valueSlots = savedPolicy; _nativeProofs = savedProofs; _usedCarrierNames = savedNames;
            _sourceSupertypes = savedGraph; _localMethods = savedMethods; _localDeclarations = savedDeclarations;
            OwnerConstrainedMethodLowering.Reset(references);
        }
        Console.WriteLine("[current physical carrier projection] self-test OK (two clocks, exact births, current-first frames, source facts and native proof continuation)");
    }
}
