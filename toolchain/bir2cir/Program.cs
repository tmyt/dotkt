// bir2cir — lower Backend IR (BIR) JSON into CLR IR (CIR) JSON.
//
// bir2cir owns the Kotlin -> CLR type substitution. Its SINGLE, sole transform rewrites the Kotlin type
// vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a BIR-SHAPED CIR (same node
// shape; only type strings change). There is no verbatim-copy / envelope alternative — that dual track is retired.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DotKt.Toolchain;

static class Bir2Cir
{
    static int Main(string[] args)
    {
        try
        {
            args = ResponseFileArguments.Expand(args);
            // The shared BIR/CIR codec (bir-common) is <Compile Link/>-shared, so it has no project of its own
            // and no test host. Running its self-tests HERE exercises the exact compiled copy this tool reads and
            // writes documents with; tests/ir/run-schema.sh drives it beside the document-level freeze checks.
            if (args.Length == 1 && args[0] == "--selftest")
            {
                TypeNodeSelfTest.Run();
                MemberRefNodeSelfTest.Run();
                DotKt.Cir.AttributeSelfTest.Run(JsonOptions.Indented);
                AttributeProjection.SelfTest();
                PInvokeLowering.SelfTest();
                TypedAttributeBinding.SelfTest();
                ForeignNullableGenericCrossing.SelfTest();
                DotKt.Cir.DeclarationSelfTest.Run();
                DotKt.Cir.ExecutableSelfTest.Run();
                DotKt.Cir.MessagePackSelfTest.Run();
                CirDeclarationProjection.SelfTest();
                CirDeclarationProjection.SelfTestExecutable();
                CirFinalizationTests.Run();
                AliasConstructorDelegationExpansion.SelfTest();
                AliasHelperHoist.SelfTest();
                DeclarationIdentityBinding.SelfTest();
                SourceDeclarationSignatures.SelfTest();
                DeclarationSignatureIdentity.SelfTest();
                ForeignStarProjectionBinding.SelfTest();
                MemberCallSubstitution.StaticCollectionPreferenceSelfTest();
                MemberCallSubstitution.SourceDeclarationSnapshotsSelfTest();
                FunctionSignatureIdentity.SelfTest();
                FunctionValueRepresentation.SelfTestSourceFacts();
                BirNodeType.SelfTest();
                CapturedGenericInvocation.SelfTestSourceFacts();
                SourceModuleIndex.SelfTest();
                InheritedClassInterfaceBridge.SourceSelfTest();
                BirFileCodec.SelfTest();
                BirGraphMutationTests.SelfTest();
                ClrTypeVariance.SelfTest();
                PhysicalValueCoercion.SelfTest();
                TypedPhysicalValueCoercion.SelfTest();
                TypedPhysicalOperandNormalization.SelfTest();
                TypedPhysicalDelegateAdapter.SelfTest();
                ClrMemberResolution.SelfTestTypedDelegateSlots();
                ClrMemberResolution.SelfTestTypedInterfaceSlots();
                BirGraphNameFieldsTests.SelfTest();
                StringCharSequenceBridge.SelfTest();
                PrimitiveOperatorLowering.SelfTest();
                SourceGraphRewrite.SelfTest();
                ForInLowering.SelfTestTypedRangePrefix();
                ArrayConstructionLowering.SelfTestSource();
                CharCodeInvokeLowering.SelfTestSource();
                NetInteropBinding.TypedSelfTest();
                RoundtripMetadata.SourceSuspendResultSelfTest();
                InheritedMemberOwnerBinding.SourceProjectionSelfTest();
                TypedExecutableFrames.SelfTest();
                ClosureSynthesis.TypedMaterializedSelfTest();
                SharedSyntheticSynthesis.SelfTest();
                DefaultArgSplice.SourcePreparationSelfTest();
                FaithfulHintRecognition.TypedSelfTest();
                KotlinPropertyAccessors.SelfTestAccessorSignatures();
                LateinitInitializationLoweringTests.SelfTest();
                CompanionExtensionBinding.SelfTest();
                LexicalDeclarationIds.SelfTest();
                ClrEventSubscriptionBinding.SelfTest();
                ClrEventImplBinding.SelfTestSourceFacts();
                ExistentialReceiverBinding.SelfTest();
                MaterializedBirPayload.SelfTest();
                InlineBirStashTests.SelfTest();
                InlineExpansion.SelfTestHygiene();
                InlineExpansion.SelfTestPrerequisites();
                PostInlineValueLoweringTests.SelfTest();
                LocalFunctionLowering.SourceSelfTest();
                ObjectSlotRename.SelfTest();
                DefaultBirCarrier.SelfTest();
                ExecutableBirCodec.SelfTest();
                MaterializedExecutable.SelfTest();
                NullableWitnessDemand.SelfTest();
                ReifiedNullabilityWitnessLowering.SourceSelfTest();
                NullableRepresentationDemand.SelfTest();
                NullableRepresentationTypes.SelfTest();
                NullableRepresentationMaterialization.SelfTest();
                EnumIntrinsicLowering.SelfTest();
                ClrEnumLowering.SelfTest();
                EnumValueLowering.SelfTest();
                EnumEntriesLowering.SelfTest();
                CompanionRepresentationLoweringTests.SelfTest();
                InnerRepresentationFrameTests.SelfTest();
                OpaqueCarrierTypeBinding.SelfTest();
                GenericRepresentationPolicy.SelfTest();
                KotlinGenericScalarSlots.SourceScalarSelfTest();
                BirTypeLowering.SelfTestSlotReturns();
                BirTypeLowering.SelfTestFunctionPointers();
                UncheckedGenericCastReturnErasure.SelfTest();
                UnsafeAccessorLowering.SuperForwarderSelfTest();
                UnsafeAccessorLowering.FamilyAccessSelfTest();
                BirTypeLowering.SelfTestMethodImplMetadata();
                SupertypeGraph.SelfTestDeclarationIdentity();
                CollectionHelperBinding.SelfTest();
                SourceCollectionInputs.SelfTest();
                IntrinsicExtensionRepresentation.SelfTest();
                ReferenceExistentialAbiBinding.SelfTest();
                KotlinCollectionSlotSynthesis.SelfTest();
                CollectionBclSlotSynthesis.SelfTest();
                KotlinOverrideSlotBridge.SelfTest();
                ContinuationErasure.SelfTest();
                ReferenceNullableStrip.FunctionSelfTest();
                StarProjectionBoundLowering.FunctionSelfTest();
                ClrMemberResolution.InheritedGenericResultSelfTest();
                ClrMemberResolution.FunctionSlotComparisonSelfTest();
                StringCharSequenceBridge.CallSignatureSelfTest();
                ClrMemberResolution.NullableSlotComparisonSelfTest();
                OwnerConstrainedMethodLowering.SelfTest();
                ConstrainedTypeParameterReceiverBinding.AritySnapshotSelfTest();
                SourceOwnerClosureTests.Run();
                StdlibSubstituteTypeParams.SelfTest();
                FBoundStarProjectionErasure.ProjectionConstraintSelfTest();
                ComparableRepresentationLowering.SelfTest();
                ValueRepresentationLowering.SelfTest();
                ValueRepresentationSourcePolicy.SelfTest();
                CallableVisibilityLowering.SelfTest();
                AliasVarianceRepresentation.SelfTest();
                ExactMutableViewDispatch.SelfTest();
                KotlinCollectionClassifierLowering.SelfTest();
                GenericArrayStorageLowering.SelfTest();
                ErasedElementValues.SelfTest();
                RoundtripMetadata.RuntimeLinkageSelfTest();
                SuspendColdLowering.DeclarationRegistrySelfTest();
                ReferenceMetadataIndex.SelfTest();
                MetadataDeclaredMembersTests.SelfTest();
                NullableTvErasureCallRealign.SelfTest();
                MapVarianceRealign.SelfTest();
                TypedModuleInventory.SelfTest();
                TypedCharSequenceIdentityTests.SelfTest();
                return 0;
            }
            var options = DriverOptions.Parse(args);
            new Pipeline(options).Run();
            return 0;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: bir2cir <out-dir> [--compile-refs <dll;dll;...>] [--build-stdlib=metadata|runtime] [--reflection-restricted] [--cir-format=json|msgpack] <file.bir.json|file.bir.msgpack>...");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bir2cir: {ex.Message}");
            return 1;
        }
    }
}

sealed class Pipeline
{
    readonly DriverOptions _options;
    bool _enumRuntimeLookup;

    public Pipeline(DriverOptions options) => _options = options;


    public void Run()
    {
        Directory.CreateDirectory(_options.OutDir);

        var refs = ReferenceMetadataIndex.Build(_options.CompileReferences);
        // Fail-loud: a ref.dll scan swallows load/type failures into Diagnostics (so ONE malformed type never aborts the
        // whole scan). Surface them here — a silent ref-scan miss otherwise surfaces as a distant EntryPointNotFound/NRE
        // with no "ref scan failed" signal. An empty Diagnostics stays silent (the happy path prints nothing).
        var diagnostics = refs.Diagnostics.ToList();
        foreach (var d in diagnostics) Console.Error.WriteLine($"bir2cir: WARNING ref-scan diagnostic: {d}");
        var module = LoadBirFiles(_options.Inputs, _options.RefBuild, _options.SubstituteStdlibBuild,
            _options.StdlibMode == BuildStdlibMode.App, refs);
        var birFiles = module.Files;
        var cirFiles = TransformFiles(module, refs);
        if (_options.ReflectionRestricted
            && (ForeignStarProjectionBinding.UsedRuntimeFallback || StarProjectionLowering.UsedRuntimeFallback))
            Console.Error.WriteLine(
                "bir2cir: warning DOTKTSTAR001: a CLR star projection uses reflection; "
                + "NativeAOT/trimming must preserve the referenced generic type and member metadata");
        if (_options.ReflectionRestricted && _enumRuntimeLookup)
            Console.Error.WriteLine(
                "bir2cir: warning DOTKTENUM001: a generic enum operation uses reflection; "
                + "NativeAOT/trimming must preserve the enum declaration and its generated API metadata");
        // Release the long-lived .NET-interop MetadataLoadContext (kept alive across all transform passes for
        // NetInteropBinding's owner resolution — A2 / #61) now that no pass needs metadata reflection.
        refs.DisposeNet();
        // #112 Phase 4: run the SHARED IR sanity gate on the CIR we just produced — the EARLIEST catch, at the
        // bir2cir/CIR boundary (ilemit re-runs the SAME bir-common IrSanity at the head of EmitAssembly). A malformed
        // CIR (undeclared local, dangling goto, missing owner) fails LOUD here with a precise invariant message
        // instead of surfacing two stages downstream. Pure fail-fast validation — no effect on a valid CIR.
        CheckCirSanity(cirFiles);
        WriteCirFiles(cirFiles);

        var suspend = SuspendShapeAnalysis.Combine(birFiles.Select(f => f.Suspend));
        Console.Error.WriteLine(
            $"bir2cir: lowered {birFiles.Count} BIR file(s) -> {_options.OutDir} ({refs.Count} ref(s), build: {(_options.RefBuild ? "reference" : "substitute/app")}, suspend: {suspend.FunctionCount} fn)");
    }

    static LoadedBirModule LoadBirFiles(IReadOnlyList<string> inputs, bool referenceBuild, bool substituteStdlibBuild, bool appBuild,
        ReferenceMetadataIndex refs)
    {
        var inputsWithSources = new List<(string Path, DotKt.Bir.Lowering.BirFileDeclaration Source, SuspendShapeAnalysis Suspend)>();
        foreach (var input in inputs)
        {
            var path = Path.GetFullPath(input);
            var source = DefaultBirCarrier.FreezeSourceDefaults(BirFileCodec.Read(path));
            var suspend = SuspendShapeAnalyzer.Analyze(source);
            inputsWithSources.Add((path, source, suspend));
        }

        IEnumerable<DotKt.Bir.Lowering.BirFileDeclaration> TakeSources()
        {
            for (var index = 0; index < inputsWithSources.Count; index++)
            {
                var input = inputsWithSources[index];
                // Transfer this root to capture instead of retaining every
                // original graph alongside its replacement and the DOM output.
                inputsWithSources[index] = (input.Path, null, input.Suspend);
                yield return input.Source;
            }
        }
        var captured = DeclarationSignatureIdentity.Capture(TakeSources());
        for (var index = 0; index < captured.Length; index++) captured[index] = SourceOwnerClosure.Apply(captured[index]);
        captured = ComparableRepresentationLowering.Apply(captured, referenceBuild);
        // Keep the selected declaration's Kotlin spelling and signature before
        // physical shaping can rename accessors, companion cores or suspend entries.
        // Later metadata must not reconstruct them from get_/set_ or CLR names.
        var signatures = new SourceDeclarationSignatures();
        captured = signatures.Capture(captured);
        var valuePolicy = new ValueRepresentationSourcePolicy(captured, refs, referenceBuild);
        for (var index = 0; index < captured.Length; index++) captured[index] = valuePolicy.EraseBounds(captured[index]);
        captured = CallableVisibilityLowering.ApplyAll(captured, refs);
        captured = FunctionValueRepresentation.PreserveSourceFacts(captured);
        captured = ClrEventImplBinding.PreserveSourceFacts(captured);
        captured = ContinuationErasure.PreserveSourceSupertypes(captured, refs);
        captured = CapturedGenericInvocation.PreserveSourceFacts(captured, refs);
        var sourceIndex = new SourceModuleIndex(captured, refs.Aliases);
        // New physical-only forwarders are not source declarations. Keep the
        // original declaration membership while extending the owning prefix.
        InheritedClassInterfaceBridge.MaterializeSuspendDeclarations(captured, refs);
        // Resolve alias constructor expressions while source bodies are still
        // owning values. Signatures and declaration membership were captured
        // above; these rewrites change uses/bodies, not that source inventory.
        ValueTypeOracle sourceIsValue = type => refs.IsValueType(type) || sourceIndex.ValueTypes.Contains(type.Name);
        var aliasConstructorDelegations = AliasConstructorDelegationExpansion.Collect(
            captured, refs, sourceIsValue, carryForReference: referenceBuild);
        if (!referenceBuild)
            foreach (var source in captured) aliasConstructorDelegations.Apply(source);
        // Snapshot source collection-selection facts before helper producers
        // change expressions; consume them before entering the DOM frontier.
        var collectionInputs = new SourceCollectionInputs(captured, refs);
        var sourceTopLevelFns = captured.SelectMany(source => source.Methods.OrEmpty())
            .Select(method => method.Name.OrDefault()).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        FaithfulHints.WithHelperBinding(collectionInputs.Helpers.Bind, () => {
            for (var index = 0; index < captured.Length; index++)
            {
                captured[index] = (DotKt.Bir.Lowering.BirFileDeclaration)DataClassArrayMemberLowering.Apply(
                    captured[index], collectionInputs.Helpers.Bind);
                captured[index] = (DotKt.Bir.Lowering.BirFileDeclaration)EnumEntriesLowering.Apply(
                    captured[index], refs, collectionInputs.Helpers.Bind);
                captured[index] = (DotKt.Bir.Lowering.BirFileDeclaration)ObjectSlotRename.Apply(captured[index]);
                captured[index] = (DotKt.Bir.Lowering.BirFileDeclaration)PrimitiveOperatorLowering.Apply(captured[index], refs);
                captured[index] = (DotKt.Bir.Lowering.BirFileDeclaration)FaithfulHintRecognition.Apply(captured[index], sourceTopLevelFns);
            }
        });
        CollectionHelperBinding.Apply(captured, refs, sourceIndex.CollisionAliases, collectionInputs);
        var nullableWitnessDemand = NullableWitnessDemand.Collect(captured, refs);
        RoundtripMetadata.FreezeSuspendResults(captured);
        InheritedMemberOwnerBinding.ProjectOwners(captured, refs);
        var genericRepresentations = GenericRepresentationPolicy.CreateSource(captured, sourceIndex.CollisionAliases, refs);
        var scalarSlots = KotlinGenericScalarSlots.ApplyAll(captured, genericRepresentations, refs);
        KotlinCollectionSlotSynthesis.EraseGenericQueryParameters(captured, refs, sourceIndex.CollisionAliases);
        var valueSources = ValueRepresentationLowering.CaptureSource(captured, sourceIndex.DeclarationIds);
        // Preparation stages the actual default occurrences while their source
        // frames still exist. Representation rewrites those owning occurrences
        // in place; restoration keeps the transformed payload, not an old copy.
        var restoreDefaultFrames = DefaultArgSplice.PrepareInlineDefaults(captured, refs);
        // Runtime bodies consume the paired reference declaration's ABI by its
        // exact source identity, independently of their own body-only demand.
        var declaredMethodFrames = substituteStdlibBuild
            ? sourceIndex.DeclarationIds.Select(id => (Id: id, Frame: refs.NullableMethodFrame(id)))
                .Where(pair => pair.Frame != null)
                .ToDictionary(pair => pair.Id, pair => pair.Frame, StringComparer.Ordinal)
            : null;
        var representationMethodFrames = NullableRepresentationMaterialization.Apply(
            captured, sourceIsValue, refs, policy: genericRepresentations, declaredMethods: declaredMethodFrames);
        restoreDefaultFrames();
        var localFactories = MemberCallSubstitution.CollectLocalFactories(captured);
        // Keep the physical placement order explicit. Each pass owns its
        // decisions; late consumers receive the resulting scalar associations.
        GenericStaticOwnerBinding.Materialize(captured);
        GenericStaticOwnerBinding.ApplyEventOwnersAll(captured, refs);
        var companionRepresentations = CompanionRepresentationLowering.Apply(captured);
        var companionExtensionBindings = CompanionExtensionBinding.Apply(captured, refs);
        LateinitInitializationLowering.ApplyAll(captured, refs);
        KotlinPropertyAccessors.AllocateDeclarationsAndProperties(captured);
        var localPropertyDeclarations = MemberCallSubstitution.CollectLocalPropertyAccessors(captured);
        genericRepresentations.PrepareDomLocations(captured);
        var placement = new DeclarationPlacement(localFactories, localPropertyDeclarations);
        var ownership = TypeOwnershipLowering.PrepareOwnershipFacts(captured);
        TypedCharSequenceIdentity.ApplyAll(captured);
        var forwardedEvents = ClrEventSubscriptionBinding.CollectForwardedOwners(captured);
        var inventory = new TypedModuleInventory(captured);
        // Current local declarations, including file facades, shadow referenced
        // CLR types before the first selected-slot resolver consumes this index.
        refs.SetLocalEmittedTypes(inventory.EmittedTypeFqns);
        // Nested invariant override slots adopt the selected declaration shape before
        // snapshots; top-level object seams remain for the later bridge owner.
        KotlinOverrideSlotBridge.PropagateErasedSlots(captured, sourceIsValue, refs, referenceBuild,
            genericRepresentations, valuePolicy.HasClassifier);
        var nullableDeclarations = NullableTvErasureCallRealign.CollectDeclaredMemberRets(captured, refs);
        var uncheckedDeclarations = UncheckedGenericCastReturnErasure.CaptureSource(captured);
        var richEnums = EnumIntrinsicLowering.CollectRichEnums(captured);
        // Snapshot producer-owned instance slots before lowering cross-file enum access.
        var richEnumFields = EnumValueLowering.CollectRichInstanceFields(captured);
        var basicEnums = EnumMemberBinding.CollectBasicEnums(captured);
        // Select exact CLR integral widths and ordered physical constants once,
        // retaining separate explicit-remapping and all-enum parsing inventories.
        var explicitEnums = ClrEnumLowering.Apply(captured, out var parseEnums);
        // Publish representation-selected inline bodies before per-file rewrites.
        // The local index retains source spans; only outgoing opaque DLL carriers
        // hide them. Later declaration/body walks do not descend into these bytes;
        // the explicit opaque-carrier binder still owns classifier binding.
        InlineBirStash.Reset();
        foreach (var source in captured) InlineBirStash.Stash(source);
        var appLocalFileClassMethods = InlineSplice.CollectAppLocalMethodNames(captured);
        var inlineDispatchHierarchy = InlineSplice.CollectDispatchHierarchy(captured);
        // Intrinsic failures and non-literal repeat lower before expansion, so
        // discarded closures remain ingredients rather than orphan TypeDefs.
        // Expand original files in order against the complete frozen stash;
        // each file drains generated helpers before its DOM continuation.
        foreach (var source in captured)
        {
            PreconditionLowering.Apply(source, inventory.LocalTopLevelFunctions, appBuild);
            RepeatInlineLowering.Apply(source, inventory.LocalTopLevelFunctions, appBuild);
            if (appBuild) CompanionRepresentationLowering.BindSpliceUses(source, refs);
            InlineSplice.Apply(source, refs, appLocalFileClassMethods, inlineDispatchHierarchy,
                companionExtensionBindings, requireCompleteDefaults: appBuild);
        }
        ValueTypeOracle isValueFqn = type => refs.IsValueType(type) || sourceIndex.ValueTypes.Contains(type.Name);
        // Expansion is complete module-wide. Consume lexical declarations and
        // companion uses before realizing their final ordered evaluation plans.
        foreach (var source in captured)
        {
            ValueJoinNullWidening.Apply(source, isValueFqn);
            TypeOwnershipLowering.PrepareSplicedOwnershipFacts(source, ownership);
            LocalFunctionLowering.Apply(source, refs, scalarSlots.ProjectMethod);
            CompanionRepresentationLowering.BindUses(source, companionRepresentations, refs, bindExternal: appBuild);
            ClrFlagsOperationLowering.Apply(source, refs);
            TypedCallEvalLowering.Apply(source, isValueFqn);
            NothingValueTermination.Apply(source);
        }
        ownership.ReleaseSourceDeclarations();
        var valueRepresentations = ValueRepresentationLowering.CreateSource(captured, refs, referenceBuild,
            valuePolicy, valueSources, genericRepresentations);
        var retiredDeclarations = new HashSet<BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        void RetireDeclaration(BirFunctionDeclaration method)
        {
            valueRepresentations.RetireSourceDeclaration(method);
            retiredDeclarations.Add(method);
        }
        // Value operations and nullable-sensitive generated frames consume the
        // actual post-inline graph before closure ingredients become TypeDefs.
        foreach (var source in captured)
        {
            if (!referenceBuild) KClassMemberBinding.Apply(source);
            valueRepresentations.BindOperations(source);
            ReifiedNullabilityWitnessLowering.Apply(source, nullableWitnessDemand, refs, valueRepresentations, RetireDeclaration);
        }
        // Ingredients become actual declarations in this same owning graph.
        // Preserve the source vectors and append synthesized types only after
        // every file's witness preparation has completed.
        var sharedCells = new List<SharedSyntheticSynthesis.CellStorage>();
        foreach (var source in captured)
        {
            ClosureSynthesis.Apply(source, refs, scalarSlots.ProjectMethod);
            SharedSyntheticSynthesis.Apply(source, refs, sharedCells);
        }
        // Classify source loops, realize range access, then consume membership
        // endpoints before materializing range values. Keep that semantic order
        // within the same owning graph; no intermediate DOM handoff is needed.
        foreach (var source in captured)
        {
            ForInLowering.Apply(source, !appBuild, inventory.DirectSuperNames, inventory.LocalTopLevelFunctions, refs);
            RangeForLowering.Apply(source, !appBuild);
            RangeMembershipLowering.Apply(source, inventory.LocalTopLevelFunctions, appBuild);
            RangeConstructionLowering.Apply(source);
        }
        var enumRuntimeLookup = false;
        foreach (var source in captured)
        {
            // Range realization can produce further primitive operations.
            PrimitiveOperatorLowering.Apply(source, refs);
            // Resolve enum values before array element derivation. EnumEntries
            // already denotes a cached list and must not become an array value.
            EnumIntrinsicLowering.Apply(source, richEnums, inventory.LocalTopLevelFunctions, appBuild, refs,
                representationMethodFrames);
            enumRuntimeLookup |= EnumValueLowering.Apply(source, refs, richEnumFields, basicEnums, explicitEnums, parseEnums);
        }
        var enums = new EnumPreparation(richEnums, basicEnums, enumRuntimeLookup);
        // Enum values now state their array type. Derive array elements and
        // realize constructors before any type erasure or suspend spill owner.
        foreach (var source in captured) ArrayConstructionLowering.Apply(source, refs);
        foreach (var source in captured)
        {
            // This late recognition deliberately runs outside helper binding:
            // its calls retain the existing subsequent exact-resolution path.
            FaithfulHintRecognition.Apply(source, inventory.LocalTopLevelFunctions);
            CharCodeInvokeLowering.Apply(source);
        }
        // Native binding consumes the same owning operations before the one
        // remaining DOM frontier. Reference builds retain Kotlin declarations.
        if (!referenceBuild)
            foreach (var source in captured) NetInteropBinding.Apply(source, refs);
        var files = new List<BirFile>();
        for (var index = 0; index < inputsWithSources.Count; index++)
        {
            var source = captured[index];
            captured[index] = null;
            // The remaining passes still own DOM graphs. Construct that frontier
            // directly; never serialize/reparse the typed input between passes.
            var root = (source with { TypeAliases = default }).ToDom();
            RoundtripMetadata.FreezeTypeAliases(root, source.TypeAliases.OrDefault() ?? []);
            files.Add(new BirFile(
                inputsWithSources[index].Path,
                root,
                inputsWithSources[index].Suspend));
        }

        var continuingRoots = files.Select(file => file.Root).ToArray();
        // Retired generated targets still supply source declaration facts. Put
        // those explicitly reported declarations through the same frontier once;
        // both continuing indexes receive the exact same detached DOM record.
        var retiredDom = new Dictionary<BirFunctionDeclaration, JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var method in retiredDeclarations) retiredDom.Add(method, method.ToDom());
        genericRepresentations.BindDomDeclarations(continuingRoots);
        valueRepresentations.BindDomDeclarations(continuingRoots, retiredDom);
        var uncheckedReturns = UncheckedGenericCastReturnErasure.BindDomDeclarations(uncheckedDeclarations,
            continuingRoots.Cast<JsonObject>().ToArray(), retiredDom);
        retiredDeclarations.Clear();
        retiredDom.Clear();
        return new LoadedBirModule(files, signatures, sourceIndex,
            genericRepresentations, scalarSlots, valueRepresentations, representationMethodFrames, placement,
            new ModulePreparation(inventory, forwardedEvents, nullableDeclarations, uncheckedReturns, enums, sharedCells));
    }

    List<CirFile> TransformFiles(LoadedBirModule module, ReferenceMetadataIndex refs)
    {
        _enumRuntimeLookup |= module.Preparation.Enums.RuntimeLookup;
        var birFiles = module.Files;
        var birRoots = birFiles.Select(b => b.Root).ToList();
        var declarationSemanticSignatures = module.Signatures;
        var valueRepresentations = module.ValueRepresentations;
        KotlinRuntimeTypeWitness.ValueRepresentations = valueRepresentations;
        var localDeclarationIds = module.SourceIndex.DeclarationIds;
        var representationAliases = module.SourceIndex.CollisionAliases;
        var localValueTypeFqns = module.SourceIndex.ValueTypes;
        ValueTypeOracle isValueFqn = type => refs.IsValueType(type) || localValueTypeFqns.Contains(type.Name);
        // A generic query implementation accepts its declared erased bound, not the closed CLR E.
        // State that ABI before frame materialization and declaration/use snapshots; typed overrides
        // retain their concrete signature and their ordinary query guard.
        var genericRepresentations = module.GenericRepresentations;
        var scalarSlots = module.ScalarSlots;
        var physicalParameterIndices = scalarSlots.Parameters;
        var representationMethodFrames = module.RepresentationMethodFrames;
        var localFactories = module.Placement.Factories;
        var localPropertyDeclarations = module.Placement.Properties;
        // A delegated CLR-event declaration and a subscription through that Kotlin wrapper may be in sibling source
        // files. Snapshot the module-wide relation before any declaration-normalizing pass can consume or rebuild the
        // kotc directive; ClrEventSubscriptionBinding later consumes this immutable physical-owner index per file.
        var clrEventForwardedOwners = module.Preparation.ForwardedEvents;

        // The top-level funs DEFINED in this compilation (every file-class's own static methods, across all input
        // files). A `callStatic owner=null` to one of these must stay owner-less (ilemit's FindStatic finds the
        // sibling); only a name absent here is eligible for referenced-stdlib file-class attribution (Gap B).
        var localTopLevelFns = module.Preparation.Inventory.LocalTopLevelFunctions;
        // The type FQNs DECLARED in this compilation (every input file's own `types`). SuspendColdLowering
        // uses it to decide whether the cold-core base (kotlin.coroutines.clr.internal.ContinuationImpl) is
        // a LOCAL type (rt-stdlib self-build -> bare base + local slot override) or a REFERENCED one
        // (app build -> clr: base + pendingOverrideOwner linkage).
        var localTypeFqns = module.Preparation.Inventory.EmittedTypeFqns;
        // Attribute referenced top-level stdlib funs to their file-class owner only in an APP build: a stdlib self-
        // build (`--build-stdlib=metadata|runtime`) defines them locally, so owner=null is correct there. The reference
        // build never runs MemberCallSubstitution at all (see the RefBuild gate below).
        var attributeTopLevelOwner = _options.StdlibMode == BuildStdlibMode.App;

        // Does THIS assembly declare a user `class S : CharSequence` (a type whose `interfaces` names the synthetic
        // `dotkt$CharSequence`)? If so, CharSequence must stay the polymorphic synthetic ASSEMBLY-WIDE: a
        // CharSequence param/local in such an assembly may hold that user impl and be read polymorphically
        // (`show(cs: CharSequence) = cs.length` with `show(S("hello"))` == 5) — collapsing it to `string` would
        // snapshot the value via `.toString()` and lose the length. So the CharSequence -> System.String lowering
        // (CharSeqStringLowering) is DISABLED for such assemblies (they keep the synthetic, unchanged), and enabled
        // only for a "pure" app assembly with no user implementer. Sealed System.String forbids a real `: string`
        // supertype, so this synthetic-retention is a technical necessity, not a preference. (docs/design-charsequence-clr-string.md)
        var hasUserCharSeqImpl = module.Preparation.Inventory.HasUserCharSeqImplementation;

        // Exact declaration identities select the callee's parameter-type vector,
        // captured module-wide before override or per-file declaration changes.
        var calleeTypeParams = module.Preparation.Inventory.CalleeTypeParameters;

        // Snapshot every LOCAL generic type's declared member returns BEFORE the per-file DEF-side EraseNullableTv
        // (NullableGenericErasure runs inside the transform loop, mutating declarations in place). Feeds
        // NullableTvErasureCallRealign so a `Box<Int>.get_a()` call across the generic boundary re-derives its
        // return from the (erased) declaration instead of kotc's over-substituted `Ref<Nullable<Int>>` (#4).
        var nullableTvDeclRets = module.Preparation.NullableDeclarations;
        // Snapshot every method whose source-level `T` return is produced by an unchecked cast from a nullable/object
        // carrier.  Its physical CLR ABI must return object so narrowing can be delayed until the value is consumed;
        // the exact Kotlin `T` return is retained through [KotlinType].  Collected module-wide so cross-file local calls
        // are matched by exact frontend declaration identity and owner, never by a library/name heuristic.
        var uncheckedGenericCastRets = module.Preparation.UncheckedReturns;

        // Rich-enum declarations require their API metadata even in a runtime
        // stdlib build; the typed prefix has already resolved their uses.
        var localRichEnums = module.Preparation.Enums.Rich;

        // The local BASIC (value-type, `kind:"enum"`) enum type names across ALL files — EnumMemberBinding rebinds a
        // `System.Enum`-inherited Object-slot call (`E.A.toString()`) on such an owner to an `objMethod`. Must be
        // module-wide (not per-file): the call site can be in a different .bir.json than the enum declaration. Only the
        // LOCAL enums need it — a CROSS-ASSEMBLY enum's inherited-member call is closed one pass earlier by
        // NetInteropBinding (reference-KLIB-projected owner -> `clrInstance`, a `constrained. callvirt`), and a klib-external
        // `kotlin.*` enum arrives from kotc already as an `objMethod`; neither reaches this local `callInstance` gap.
        var localBasicEnums = module.Preparation.Enums.Basic;

        // The local reference TYPES (classes + interfaces) module-wide (name -> declared universal slots + base) —
        // AnySlotRebind rebinds a dead-ending `callInstance <UserType>.GetHashCode/ToString/Equals` (a fake override
        // inherited from the implicit kotlin.Any, which ilemit cannot resolve because the base field is absent) to an
        // `objMethod`, exactly as EnumMemberBinding does for value-type enums (#96). Module-wide because the call site
        // may live in a different .bir.json than the type declaration, while ilemit's emitted-type table is assembly-wide.
        var localRefTypes = AnySlotRebind.CollectLocalTypes(birFiles.Select(f => f.Root));

        // The static members every LOCALLY DECLARED type carries, module-wide: a `C.f()` call site may live in another
        // .bir.json than C's declaration. Feeds LocalStaticOwnerBinding, which moves such a call's declaring type onto
        // the CLR owner axis before the ownerless (reference-universe) recognizers get a look at it.
        var localStatics = LocalStaticOwnerBinding.Collect(birFiles.Select(f => f.Root));
        // Property-reference values may be written into a function-typed field declared by a sibling BIR file.
        // Preserve the module's declared field slots before per-file lowering so the representation boundary never
        // has to infer a field type from a use or from a generated property's physical class.
        var propertyFunctionFields = PropertyReferenceFunctionLowering.CollectFieldSlots(
            birFiles.Select(f => f.Root));

        var genericDowncastHierarchy = GenericDowncastRealignment.Collect(birFiles.Select(f => f.Root));
        var iterableHierarchy = SupertypeGraph.Collect(birFiles.Select(f => f.Root));
        // PHASE 1: per-file transforms up through the CharSequence bridge. Collect the staged roots so the
        // suspend cold lowering can run GLOBALLY (a same-assembly cross-file suspend call keeps `owner:null`,
        // so its cold-entry callee may live in another file — the suspend-member registry spans all files).
        var staged = new List<(JsonNode Root, string OutputName)>();
        var localEventDeclarations = new ClrEventImplBinding.LocalEvents(birFiles.Select(file => file.Root), refs.IsByRefLikeFqn);
        var sharedCells = module.Preparation.SharedCells;
        foreach (var bir in birFiles)
        {
            var outputName = OutputNameFor(bir.Path);
            // Owning typed preparation materialized heap ref-cell declarations. Preserve their pristine element
            // slots in the nullable-generic use index before the declaration erasure below. Otherwise a late
            // inherited protected-property read may be correctly realigned to object[] while its store into a
            // concrete Array<R?> ref-cell remains invisible to the write axis (invalid object[] -> R[] CIL).
            NullableTvErasureCallRealign.CollectNewSyntheticTypes(bir.Root, nullableTvDeclRets);
            // PROPERTY-REFERENCE FUNCTION MATERIALIZATION (#242): kotc emits a genuine KProperty construction plus
            // its callable KProperty interface fact. Once every inline/default payload has been spliced and .NET calls
            // expose their declared parameter vectors, adapt any such value that fills a function-typed slot to a
            // forwarding CLR closure. The walk is structural over locals/fields/returns/call and delegate arguments/
            // array writes/joins, so one representation rule covers aliases, vararg packs, and nested values alike.
            PropertyReferenceFunctionLowering.Apply(bir.Root, propertyFunctionFields);
            // A Kotlin static member call whose declaring type is emitted by THIS compilation: the declaring identity
            // IS the CLR owner, so move it onto the owner axis now — after the .NET binder has had first refusal and
            // before the ownerless recognizers below, which resolve names against the reference universe and must
            // never be offered a call whose owner is already known. Runs in every build: a ref build declares the same
            // statics, and its bodies are squashed later.
            LocalStaticOwnerBinding.Apply(bir.Root, localStatics);
            if (!_options.RefBuild) CompanionRepresentationLowering.AssertNoCompanionValues(bir.Root);
            // #11/#501 — VALUE-TYPE PLATFORM SLOT ACCESS COERCION: reconcile reflected bare/Nullable<V> property or
            // field slots with the Kotlin platform view on both writes and reads. Writes receive explicit wrap/unwrap;
            // reads carry the reflected physical result so the receiving local cannot reinterpret Nullable<V> as V.
            // Runs right after NetInteropBinding and before BirTypeLowering. Non-ref only.
            if (!_options.RefBuild) ValueSlotNullableWrite.Apply(bir.Root, refs, isValueFqn);
            // W1-S1 (#46/#44): the `clrGeneric*` overload-matcher is now the STRUCTURED `resolvedMemberParams` descriptor
            // NetInteropBinding carries (the callee's declared param TypeNodes) — BirTypeLowering lowers it and ilemit
            // exact-matches it. The retired ShapeSynthesis pass (lossy `shapes` string derived off the @ClrTypeAlias
            // index) is DELETED; ilemit no longer re-resolves the overload by name/arity/shape-string.
            // KOTLIN COVARIANCE OVER A VALUE ELEMENT at an object-erased `Iterable<T?>` slot: `List<Int>` IS an
            // `Iterable<Int?>` in Kotlin, while an `IReadOnlyList<int32>` is not an `IEnumerable<object>` on the CLR
            // (a reified argument is invariant for a value type), so the callee's `GetEnumerator` is not found. Wrap
            // that argument in `Enumerable.Cast<object>`, which boxes each element into a real object-enumerable.
            // Only an `Iterable<T?>` slot, per position — that is the one slot the wrap's own `IEnumerable<object>`
            // inhabits. Read the materialized method frame's argument at the slot's exact index, rather than
            // expecting the source Nullable(Tv) spelling to survive physical frame allocation.
            if (!_options.RefBuild) ValueElementIterableCoercion.Apply(bir.Root, isValueFqn, iterableHierarchy, refs);
            // ARRAY-ELEMENT CANONICALIZATION (#86 D2): an `Array<X?>` with a possibly-value `X` is `object[]`, so an
            // array CREATION filling such a slot allocates `object[]` too. kotc writes the source's own element there
            // (`arrayOf(1,2,3)` into an `Array<Int?>` says `kotlin.Int`), which is not a `Nullable(...)` the erasure
            // sweep below could see — leaving it allocates an `int32[]` under an `object[]` slot, and the boxed
            // element stores then corrupt memory rather than failing to type-check. Runs BEFORE type lowering (elem
            // tokens are still `kotlin.*`) and before the erasure, which then finds the two already agreeing.
            ArrayNullableElemCanonicalization.Apply(bir.Root, isValueFqn);
            // NULLABLE IS-TEST (`x is T?`): null IS a member of a nullable type in Kotlin, and the frontend's
            // else-branch smart-cast to a NON-null `x` depends on it. `isinst` never matches null, so mark the node
            // and let ilemit add the null-accepting branch. Runs BEFORE type lowering, which erases the `?` on the
            // type operand (every CLR reference is nullable, so the lowered type cannot carry the signal), and AFTER
            // InlineSplice so a spliced inline body's own is-tests are marked too.
            NullableIsInstMatch.Apply(bir.Root);
            // Bind Enum members before alias/helper substitution. In particular an Any.toString slot selected
            // through Enum<T> must dispatch on the actual native or rich enum receiver.
            if (!_options.RefBuild) EnumMemberBinding.Apply(bir.Root, localBasicEnums);
            // Bind reference-type Object slots before physical receiver substitution. Local inherited slots
            // and virtual ToString on imported existential carriers dispatch on the actual receiver.
            if (!_options.RefBuild) AnySlotRebind.Apply(bir.Root, localRefTypes, refs);
            if (!_options.RefBuild) KotlinEqualityLowering.Apply(bir.Root);
            // NULLABLE-GENERIC erasure — THE DECLARATION AXIS of #86's erasure invariant (ALL builds, so ref.dll +
            // rt.dll + the app's view of a signature agree). `Nullable(Tv)` (a nullable UNCONSTRAINED type variable
            // `T?`) has no CLR type of its own: `Nullable<T>` is inexpressible for an unconstrained T, and a bare `!T`
            // slot collapses a null to `default(T)` = 0 at a value instantiation (firstOrNull on a value-type list
            // returning 0 rather than null-for-empty; `ldnull` into an int32 slot failing JIT verification). The one
            // CLR-faithful representation is `System.Object` — the spec-defined boxed form of `Nullable<V>`, which
            // carries a real null for a value AND a reference instantiation alike. This pass applies
            // `physical(s) = Erase(declaredKotlinType(s))` to EVERY declaration slot (return, method param, ctor param,
            // field, property, body local, nested type-arg, array element, function-type position, call `sig`) and
            // records the pre-erasure Kotlin type — plus, at an erased HEAD, its NRT byte — for the
            // [KotlinNullableGeneric] round-trip. Runs BEFORE the rest so type-lowering/substitution see it.
            if (attributeTopLevelOwner) ClrMemberResolution.CaptureReferencedStaticCallSignatures(bir.Root);
            // A suspend result is Kotlin declaration meaning. Freeze it while the frontend TypeNode is still pristine;
            // NullableGenericErasure below is allowed to change the declaration's physical CLR representation but not
            // the cross-module fact. Later synthesis must copy this explicit fact rather than reconstructing it from an
            // already-erased `suspendRet`.
            RoundtripMetadata.FreezeSuspendResults(new[] { bir.Root });
            NullableGenericErasure.Apply(bir.Root, isValueFqn);
            FunctionValueRepresentation.Apply(bir.Root, refs.IsByRefLikeFqn);
            if (!_options.RefBuild)
                ErasedElementValues.ApplyAll(new[] { bir.Root }, refs, isValueFqn, genericRepresentations,
                    call => scalarSlots.Result(call, refs), scalarCallsOnly: true);
            // GENERIC-BOUNDARY nullable-Tv USE realignment — THE USE AXIS of #86's erasure invariant (#4;
            // #113/#117/#120/#142). The DEF-side erasure above turns a member's `T?`/`…Ref<T?>…` into
            // `object`/`…Ref<object>…`, but a CALL site kotc emitted with T already substituted carries the concrete
            // `Nullable(kotlin.Int)`/`…Ref<Nullable(kotlin.Int)>…` (no bare `Tv` for the sweep to catch), which lowers
            // to a slot the member neither returns nor accepts — an ilverify StackUnexpected at a nested position, a
            // member that does not exist at all when the call's `sig` descriptor is what drifted. So every USE is
            // re-derived as `Subst(Erase(declaration), typeArgs)` and never `Erase(Subst(...))`: call returns, call
            // and constructor ARGUMENTS (descriptor included), field reads, and the WRITE positions — setLocal,
            // setField (statement/expression/static forms), arraySet, return (statement/expression forms), and the
            // `if/else` value join. Object-erasure rewrites stay gated to the exact bare-object boundary; concrete
            // V <-> Nullable<V> fixed slots use explicit construction/extraction with complete type identity.
            // BEFORE BirTypeLowering.
            NullableTvErasureCallRealign.ApplySourceUses(bir.Root, nullableTvDeclRets, isValueFqn, refs);
            // DELEGATE-TARGET slot alignment (ALL builds — the declaration half neither axis above can reach on its
            // own): a delegate's parameters and return are reified ARGUMENTS, so `(Int?) -> String` is
            // `Func<object, string>` and `(T?) -> String` is `Func<object, string>` at every instantiation; the
            // LIFTED method bound into that delegate declares ordinary slots, where a direct `Int?` is a
            // `Nullable<int32>` and a `String?` is a `string`. ECMA-335 II.14.6 admits neither pair, so the target's
            // slot follows the delegate's `object` — every parameter (contravariant: only `object` is assignable
            // from `object`) and a value/`Nullable`/type-variable return (covariant: a reference already reaches
            // `object`, and rewriting it is what broke #189). Runs AFTER the use axis, which is what corrects a
            // construction's own `funcType` to the slot it fills; the declared transition below then types the newly-`object`
            // lambda slots' BODIES — narrowing each read and boxing each `return` — and happens only when a slot
            // actually moved.
            if (DelegateTargetSlotAlignment.Apply(bir.Root, isValueFqn))
                NullableTvErasureCallRealign.ApplyAfterDelegateSlotAlignment(
                    bir.Root, nullableTvDeclRets, isValueFqn, refs);
            // UNCHECKED OBJECT->Tv RETURN ERASURE: the non-null-T sibling of nullable-generic return erasure.  A JVM
            // `Any? as T` physically returns Object; spelling the CLR return as reified T would insert `unbox.any T`
            // inside the callee and throw even when a null result is stored but never consumed.  Emit object physically,
            // preserve T in round-trip metadata, and keep a directly initialized Tv local object-typed until its actual
            // typed use.  All matching is structural and this pass fully states the CIR types ilemit emits 1:1.
            UncheckedGenericCastReturnErasure.Apply(bir.Root, uncheckedGenericCastRets);
            // VARIANCE -> INVARIANCE type-arg REALIGNMENT (il-bymap): kotc approximates a use-site `in`/`out` variance
            // projection to `kotlin.Any` (JVM-erased, harmless), so a call into an INVARIANT @ClrTypeAlias collection
            // generic (`getOrImplicitDefault<K,V>` on a `Map<String,V>` receiver) carries a `K = Any` typeArg while the
            // actual arg pins `K = String`. On the CLR `IDictionary<,>` is invariant, so the mismatch -> EntryPointNotFound
            // at `ContainsKey`. Realign each such typeArg to the actual argument's concrete type-argument. BIR-space,
            // before MemberCallSubstitution + type lowering; non-ref only. A no-op when the arg already agrees (genuine
            // `<Any>` calls) or the callee isn't a local input (an app never re-lowers a referenced stdlib body).
            if (!_options.RefBuild) MapVarianceRealign.Apply(bir.Root, calleeTypeParams, refs);
            // CALL substitution (substitute/app builds only): a member call / construction whose OWNER is a CLR-bound
            // type in the ref.dll (@ClrTypeAlias, or the legacy class-level @ClrIntrinsic) is rewritten to a plain BCL
            // call/new. This is the bir2cir home of what kotc's clrName() member routing used to do — sourced from the
            // ref.dll's @ClrIntrinsic labels, NOT from kotc. Runs BEFORE type lowering so it sees the kotlin.* owners.
            // RULE-3 HOIST (all CLR-bound alias classes): kotc emits EVERY @ClrTypeAlias class with hoistable bodies as a
            // PLAIN BIR type — alias-only files (String/Char/Boolean) AND the MIXED files (StringBuilder/collections/
            // Regex/unsigned) alike — and synthesizes NO dotkt$ClrH_* helper itself. This pass reads the ref.dll
            // @ClrTypeAlias index, turns each such plain type into the static helper + drops it, BEFORE call substitution
            // so the (already-BCL) member bodies and the rule-3 call routing both see a consistent helper. No-op for ref.
            // MEMBER-STRIP (clrName migration): drop the @ClrIntrinsic-bound stub declarations kotc used to exclude
            // (once it stops reading @ClrIntrinsic). BEFORE the hoist so an alias class's bound stubs / @ClrIntrinsic
            // overrides don't over-hoist into the rule-3 helper.
            if (!_options.RefBuild) MemberStrip.Apply(bir.Root, refs);
            // Kotlin IR fake overrides are BIR facts, not CLR declarations. Consume the frontend's explicit selected
            // implementation fact instead of rediscovering a DIM from hierarchy bodies or reference metadata.
            // This is representation-independent and therefore applies to reference builds too.
            InheritedDefaultFakeOverrideElision.Apply(bir.Root);
            var hoisted = _options.RefBuild ? bir.Root : AliasHelperHoist.Apply(bir.Root, refs, genericRepresentations);
            // CLR override allocation: ordinary functions carrying @ClrIntrinsic receive the external slot name;
            // Kotlin property accessors keep their dedicated name and receive an explicit interface/base MethodImpl
            // binding instead. Derived from the frontend's `overrides` closure plus reference metadata. Runs before
            // MemberCallSubstitution so CLR-bound calls can still be shaped from the exact external identity. Never in
            // ref builds, whose declarations remain a pure Kotlin surface.
            // Bind the selected reference declaration before mapping its inherited CLR slot. Rebinding a
            // reference-stub name after DeclarationRename would undo that physical override decision.
            DeclarationIdentityBinding.BindReferenced(hoisted, refs, localDeclarationIds, deferUnknown: true);
            if (!_options.RefBuild) DeclarationRename.Apply(hoisted, refs);
            // STAR-PROJECTION COLLECTION CLASSIFIERS: use faithful non-generic BCL faces where one exists; otherwise
            // author the Collection/Set/MutableSet composite classifier plus the following smart-cast member access.
            // App build only, before MemberCallSubstitution while the Kotlin owner is still visible.
            if (!_options.RefBuild)
            {
                KotlinRuntimeTypeWitness.LowerOperations(hoisted, refs);
                KotlinCollectionClassifierLowering.Apply(hoisted, refs);
            }
            if (attributeTopLevelOwner) StarProjectionLowering.Apply(hoisted, refs);
            // EVENT HANDLES: materialize each clrEventGet as an ordinary stdlib ClrEvent<H> construction with
            // add/remove callback closures. Subscribe is a normal Kotlin member call, independent of whether its
            // receiver was read directly, stored, returned or passed across a DLL. Run before member substitution
            // so the new construction and callbacks receive the same ordinary physical binding as source code.
            hoisted = ClrEventSubscriptionBinding.Apply(
                hoisted, refs, clrEventForwardedOwners, out var materializedEventHandles);
            foreach (var materialized in materializedEventHandles)
                FunctionValueRepresentation.Apply(materialized, refs.IsByRefLikeFqn);
            // Event binding has just synthesized receiver locals plus add/remove member accesses. Feed those
            // NEW fixed slots through the same nullable-value use-axis rule as the original tree. In particular, a
            // proven-present Nullable<V> event receiver must enter the synthesized bare-V spill as V; the first pass
            // could not see a node that did not exist yet. Submit the exact materialized work items rather than
            // repairing the whole file after construction.
            NullableTvErasureCallRealign.ApplyMaterialized(
                materializedEventHandles, nullableTvDeclRets, isValueFqn, refs);
            // Event reads synthesize add/remove callbacks as normal `newClosure` ingredient bags. The main
            // ClosureSynthesis pass ran earlier, before event binding. Give the exact materialized roots to its late
            // entry contract so only these newly-created callback classes are assembled.
            var materializedEventClosureTypes = ClosureSynthesis.ApplyMaterialized(
                hoisted, materializedEventHandles, refs, scalarSlots.ProjectMethod);
            if (DelegateTargetSlotAlignment.ApplyMaterialized(
                    materializedEventHandles, materializedEventClosureTypes, isValueFqn))
                foreach (var materializedType in materializedEventClosureTypes)
                    NullableTvErasureCallRealign.ApplyAfterDelegateSlotAlignment(
                        materializedType, nullableTvDeclRets, isValueFqn, refs);
            // ClosureSynthesis stamps the transient lifted-frame correspondence on a GENERIC closure class, and the pass
            // that consumes it (SharedSyntheticSynthesis) already ran. Drop it here so the invariant "it never reaches
            // CIR" holds for a class assembled by this late pass too, rather than only for the main one.
            foreach (var materializedType in materializedEventClosureTypes)
                SharedSyntheticSynthesis.DropSyntheticTypeArgs(materializedType);
            // .NET EVENT IMPLEMENT/RAISE (§4.2/§4.3): a Kotlin class implementing/declaring a CLR event via `by clrEvent()`.
            // kotc synthesized add_/remove_/raise_<E> + a `clrEvents` backing directive (pure-Kotlin identities); this pass —
            // the ref.dll-reading layer — resolves the concrete delegate `D` (the interface event's EventHandlerType) and
            // rewrites field-like accessor bodies to CIR `clrEventAccessorImpl` directives + inserts `<E>$delegate : D`,
            // or routes custom accessors through ordinary calls on the retained Kotlin policy field, plus
            // a type-level `clrEventDecl`. Raise sites bind module-wide after inner owner projection. App/rt only (no .NET events
            // in the ref/rt stdlib self-build).
            if (!_options.RefBuild) hoisted = ClrEventImplBinding.BindImplementations(hoisted, refs, localEventDeclarations);
            // Consume CharSequence property semantics while calls still carry the explicit Kotlin property name and
            // accessor role. MemberCallSubstitution is the physical binding boundary for those calls; no later pass may
            // recover `length` from the allocated MethodRef spelling.
            CharSeqStringLowering.CharSeqRetLambdas charSeqRetLambdas = null;
            if (!_options.RefBuild && attributeTopLevelOwner && !hasUserCharSeqImpl)
                hoisted = CharSeqStringLowering.Apply(hoisted, localTopLevelFns, out charSeqRetLambdas);
            var substituted = _options.RefBuild ? hoisted : MemberCallSubstitution.Apply(hoisted, refs,
                localTopLevelFns, attributeTopLevelOwner, isValueFqn, localPropertyDeclarations, genericRepresentations,
                localFactories, localDeclarationIds);
            // Reified-nullability witnesses were prepared while declaration identities and Kotlin type arguments were
            // still authoritative. Materialize them only after semantic calls (enum/array/collection intrinsics) have
            // either been replaced or deliberately retained, so a physical hidden ABI argument cannot interfere with
            // recognition of the Kotlin-visible argument vector.
            ReifiedNullabilityWitnessLowering.MaterializeCallWitnesses(substituted);
            // Cross-module half of UncheckedGenericCastReturnErasure.  MemberCallSubstitution has now attributed a
            // referenced top-level call to its real file-class owner; bind the trusted physical-Object/logical-T
            // metadata boundary to an explicit CIR return conversion before `sty` is consumed by type lowering.
            if (!_options.RefBuild) UncheckedGenericCastReturnErasure.ApplyReferenced(substituted, refs);
            // Cross-module half of the nullable-Tv use realignment (#86 D1). The pass above already re-derived every use
            // whose callee it could name; a REFERENCED top-level fun reaches it as `callStatic owner=null` — kotc has no
            // file class for it — and only MemberCallSubstitution has now attributed it to the owner the reference index
            // is keyed by. Same formula, same code, run once more so those calls' returns and arguments are derived from
            // the producing assembly's declaration too. Idempotent on everything the earlier run already corrected.
            if (!_options.RefBuild)
                NullableTvErasureCallRealign.ApplyAfterReferencedOwnerBinding(
                    substituted, nullableTvDeclRets, isValueFqn, refs);
            // Gap A — the for-loop iterator protocol over a referenced collection: re-point the desugared `<iterator>`
            // var + its synthetic hasNext/next owner at the REAL referenced kotlin.collections.Iterator<E> (app build
            // only; the stdlib self-build emits Iterator itself, so it is left synthetic there).
            if (attributeTopLevelOwner) IteratorConsumerNormalization.Apply(substituted);
            // String -> CharSequence adapter bridge: materialize a bare `System.String` flowing into a synthetic
            // `dotkt$CharSequence` slot as `new dotkt$StringCharSequence(str)` (String is sealed, can't implement
            // the synthetic interface). Runs on EVERY non-ref build — app AND the RT stdlib self-build. The RT build
            // NEEDS it too: the stdlib's own CharSequence-extension bodies widen a `String` into a `dotkt$CharSequence`
            // slot INTERNALLY (`CharSequence.indexOf(string: String)` -> the private `indexOf(other: CharSequence)`;
            // `String.trim()` -> `(this as CharSequence).trim()`), and without the wrap those compiled rt.dll bodies pass
            // a raw String where the interface is required -> InvalidProgram / EntryPointNotFound at run. The adapter is
            // synthesized in the rt assembly exactly once (dedup), implementing the RT's canonical `dotkt$CharSequence`,
            // so an app that then routes a String op to a real stdlib body works. Skipped only for the ref build (its
            // bodies are squashed to `throw` anyway). Purely additive: only positively-String values are wrapped.
            // CharSeqStringLowering already selected the pure-app System.String representation while calls still
            // carried explicit property identity. Materialize any remaining synthetic/StringBuilder adapters after
            // call substitution; a user CharSequence implementation keeps the synthetic representation verbatim.
            IReadOnlyList<JsonNode> materializedDelegateAdapters = Array.Empty<JsonNode>();
            if (!_options.RefBuild)
                substituted = StringCharSequenceBridge.Apply(
                    substituted, refs, charSeqRetLambdas, out materializedDelegateAdapters);
            // The String/CharSequence call-boundary bridge may materialize a closure that captures a non-literal
            // `(P...) -> String` delegate and exposes `(P...) -> CharSequence` (#190). Assemble those late closure
            // ingredients now; all earlier source/inline/event closures were already consumed by the main passes.
            var materializedDelegateAdapterTypes = ClosureSynthesis.ApplyMaterialized(
                substituted, materializedDelegateAdapters, refs, scalarSlots.ProjectMethod);
            foreach (var materializedType in materializedDelegateAdapterTypes)
                SharedSyntheticSynthesis.DropSyntheticTypeArgs(materializedType);
            // CATCH-CLAUSE WIDENING (bundle-6 ④): a Kotlin `catch (IndexOutOfBoundsException)` @ClrTypeAlias-es to a
            // SINGLE .NET type, but .NET index ops throw TWO distinct ones (List.get_Item -> ArgumentOutOfRangeException,
            // array -> IndexOutOfRangeException). Expand each such clause into two clauses covering BOTH so the Kotlin
            // "one catch handles any out-of-range access" semantics hold. Non-ref only (the ref surface stays pure Kotlin).
            if (!_options.RefBuild) CatchClauseWidening.Apply(substituted);
            staged.Add((substituted, outputName));
        }

        // Event subscriptions can target a Kotlin-declared event in a sibling source file. Every
        // implementation now has its concrete delegate-derived declaration, so bind all call sites to those exact
        // module-wide facts before type lowering.
        if (!_options.RefBuild)
        {
            ClrEventImplBinding.BindLocalSubscriptionsAll(staged.Select(s => s.Root));
        }

        // Type-level #29 source truth must be captured at the last all-Kotlin boundary. Every source/inline type now
        // exists, while inner applications, star projections, reference nullability, and captured type-parameter
        // indexes still retain their Kotlin meaning. Those facts have no separate supertype-edge metadata channel.
        if (attributeTopLevelOwner)
            foreach (var stagedFile in staged) CollectionIdentityRecord.RecordTypeEdges(stagedFile.Root);

        // Kotlin classifier meaning is fixed by this complete source/inline supertype graph, before ownership
        // and existential lowering manufacture operational CLR faces. Referenced Kotlin owners already carry
        // their declaration-owned identities; only actual foreign bases contribute CLR classifier policy.
        if (!_options.RefBuild)
            KotlinCollectionIdentitySynthesis.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        foreach (var stagedFile in staged)
            BirTypeLowering.ValidatePointerVocabulary(stagedFile.Root, stagedFile.OutputName);

        // All source and inline-spliced Kotlin type applications now exist. Project Kotlin inner argument order to
        // CLR flattened nested order before the first CLR-oriented generic/slot pass consumes those applications.
        FBoundStarProjectionErasure.RecordArrayTypeEdges(staged.Select(s => s.Root).ToList());
        // Declaration selection needs Kotlin classifier spelling and argument order, not the metadata spelling
        // or the physical existential/inner representation produced below. Keep these facts within this phase.
        var overrideSourceParameters = KotlinOverrideSlotBridge.CaptureSourceParameters(staged.Select(s => s.Root));
        TypeOwnershipLowering.RecordNestedSourceTypes(staged.Select(s => s.Root).ToList(), refs);
        TypeOwnershipLowering.ProjectInnerApplications(staged.Select(s => s.Root).ToList(), refs);
        NullableTvErasureCallRealign.ProjectDeclarationApplications(nullableTvDeclRets);
        // A referenced raise's exact native signature is in flattened CLR order. Resolve it only after the
        // declaring owner's Kotlin inner application has been projected, before later value-view erasure.
        if (!_options.RefBuild) ClrEventImplBinding.BindRaisesAll(staged.Select(s => s.Root), refs);

        // Preserve open array and iterator elements as ordinary erased values before array materialization
        // and constrained-receiver binding. Exact native locations are allocated separately by
        // the final value-flow pass; no declaration signature or allocation witness changes.
        if (!_options.RefBuild)
            ErasedElementValues.ApplyAll(staged.Select(s => s.Root), refs, isValueFqn, genericRepresentations);

        // F-BOUND STAR PROJECTION: CLR has no legal/reified `Node<*>` TypeSpec for `Node<N : Node<N>>`.
        // Materialize a deterministic non-generic existential view in bir2cir and make every closed Node<N> implement
        // it. Runs before interface-slot normalization and suspend lowering, in ref and runtime builds alike.
        CapturedGenericInvocation.CaptureParameterWitnesses(staged.Select(s => s.Root));
        var physicalDeclarationSources = new Dictionary<string, string>(StringComparer.Ordinal);
        genericRepresentations.RecordNativeFields(staged.Select(s => s.Root));
        var localExistentialOwners =
            FBoundStarProjectionErasure.ApplyAll(staged.Select(s => s.Root).ToList(), refs, sharedCells,
                physicalParameterIndices, physicalDeclarationSources, genericRepresentations);
        // Closure/SAM synthesis ran while captures still carried their Kotlin constructed types, and suspend-lambda
        // state machines will copy those same declarations later. An erased smart cast can now deliver an existential
        // carrier to generated storage; make each capture declaration/construction/field agree on that exact physical
        // representation before state-machine materialization and final member binding.
        ExistentialCaptureAlignment.ApplyAll(
            staged.Select(s => s.Root).ToList(), localExistentialOwners, refs, isValueFqn, genericRepresentations,
            (owner, field, position, arity, storage) => NullableTvErasureCallRealign.RebindGeneratedCapture(
                nullableTvDeclRets, owner, field, position, arity, storage));
        // KOTLIN FAKE-OVERRIDE -> CLR INTERFACE SLOT: if a concrete class implements an interface using a public
        // NON-VIRTUAL method inherited from its base class, Kotlin considers the member implemented but CLR implicit
        // interface binding does not. Materialize the exact forwarding member in CIR/BIR-space before suspend lowering
        // (so a synthesized suspend member is transformed normally). Exact signature/return only; ambiguity is skipped.
        InheritedClassInterfaceBridge.ApplyAll(staged.Select(s => s.Root).ToList());

        // KOTLIN COVARIANT OVERRIDE -> EXACT CLR METHODIMPL: preserve the Kotlin declaration's narrow return and add a
        // private forwarding bridge with the interface slot's exact return. The bridge carries a resolved
        // `clrInterfaceImpls` instruction; ilemit only consumes that instruction and does not infer covariance.
        var covariantBridgedSlots = CovariantInterfaceReturnBridge.ApplyAll(
            staged.Select(s => s.Root).ToList(), refs, isValueFqn, genericRepresentations, overrideSourceParameters,
            _options.RefBuild ? null : StringCharSequenceBridge.ApplyMaterializedReturns);

        // Covariant bridges own their exact cold and Task slots. Prepare only the remaining suspend-result
        // obligations, so both passes cannot claim the same interface MethodImpl.
        KotlinOverrideSlotBridge.PrepareSuspendValueBridges(
            staged.Select(s => s.Root).ToList(), isValueFqn, refs, localTypeFqns, _options.RefBuild,
            genericRepresentations, covariantBridgedSlots, overrideSourceParameters);

        // KOTLIN-ONLY COLLECTION SLOTS -> EXACT CLR METHODIMPL: the BCL operational faces carry neither Kotlin's
        // remove-capable `MutableIterable.iterator()` return nor `MutableCollection.removeAll`/`retainAll`/
        // `addAll(elements)` / `MutableList.addAll(index, elements)`. A Kotlin class overriding one of them would
        // therefore be unreachable through that face. Give each participating class the matching compiler-owned slot
        // interface plus a private forwarding bridge carrying a resolved `clrInterfaceImpls` instruction; the
        // `ClrCollectionDefaults` dispatchers test for those interfaces and otherwise run the BCL default. HERE, in
        // the Kotlin-vocabulary phase, because the pass keys on the frontend `overrides` identity — which does not
        // survive to CIR — and on Kotlin's own supertype graph. Non-ref builds only.
        if (!_options.RefBuild)
            KotlinCollectionSlotSynthesis.ApplyAll(staged.Select(s => s.Root).ToList(), refs, isValueFqn);
        valueRepresentations.SynthesizeSlots(staged.Select(s => s.Root).ToList(), isValueFqn);

        // CONSTRUCTED MEMBER RESULT SUBSTITUTION (early): suspend lowering copies a call's result type into
        // state-machine fields/locals. Close every already-constructed receiver-relative return BEFORE that copy
        // happens (`Deferred<Int>.await(): type-TV0` -> `Int`), otherwise a non-generic SM permanently captures an
        // out-of-scope TV and ilemit can only realize it as object. Later exact declaring-owner transitions discharge
        // this same obligation locally when they change a call's owner; no module-wide repair sweep follows.
        ConstructedMemberReturnSubstitution.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.5 — SUSPEND COLD LOWERING (R1 classifier): rewrite EVERY declared `suspend fun` (top-level statics,
        // extensions, instance members, static/companion members, abstract/interface members) into the cold
        // Continuation shape (SM class + `f$dotkt_suspend` cold entry + Task bridge + suspend-main drain), and rewrite
        // member/cross-file/cross-assembly suspend CALLS to the callee's cold shape. Runs GLOBALLY across all files (a
        // same-assembly cross-file suspend call keeps `owner:null`, so the registry spans every input file). After call
        // substitution (its synthesized calls are already-final sibling/BCL shapes) and before type lowering (its
        // kotlin.* type tokens flow through BirTypeLowering). Declaration is UNCONDITIONAL: a non-segmentable v1 shape
        // gets a call-time-throw cold entry, not a drop — no `suspend:true` survives to ilemit in app builds.
        //
        // Runs in app, runtime-stdlib, AND reference builds. Reference emission replaces bodies with throw stubs, but
        // must retain every declaration signature and trusted identity carrier, including the independently allocated
        // `id|cold` entry. Otherwise a consuming module can preserve the frontend-selected suspend overload only until
        // cold lowering, then has no authoritative physical binding and falls back to the erased overload set. The
        // runtime and reference builds therefore execute the same declaration transform and physical allocation.
        var suspendCalleeRet = SuspendColdLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs,
            localTypeFqns, attributeTopLevelOwner, localExistentialOwners, isValueFqn, declarationSemanticSignatures,
            physicalParameterIndices, physicalDeclarationSources);

        // PHASE 1.6 — SUSPEND LAMBDA LOWERING (bundle-6 P3 wave-2b, LIVE): replace each `newSuspendLambda`
        // node with `new <mangled>_lambdaN$sm(captures..., null)` + synthesize its SuspendLambda state machine
        // (SuspendColdLowering.BuildLambdaSm, the shared FunGen machinery). Runs after the cold lowering (so a
        // suspend-lambda relocated into a synthesized SM invokeSuspend body is still caught — this pass walks
        // the newly-added SM types too) and before type lowering. kotc emits `newSuspendLambda` for every
        // `suspend` lambda literal (exercised by cases/il-lam1, il-lam2); same (non-ref) gate as the cold lowering.
        if (!_options.RefBuild)
            SuspendLambdaLowering.ApplyAll(staged.Select(s => s.Root).ToList(), localTypeFqns, suspendCalleeRet, refs);

        // PHASE 1.7a — RESIDUAL SUSPEND DECLARATIONS. The two phases above leave the `suspend` modifier on exactly the
        // declarations the stdlib self-build retains without a state machine (the Kotlin surface kept beside its cold
        // entry, and the inline coroutine primitives whose call sites are reconstructed inline). Those have no
        // state-machine body, so bir2cir states their physical one — an explicit call-time throw — instead of letting a
        // later layer invent a body for a modifier it cannot interpret. In an app build every suspend declaration is
        // lowered, so a survivor is refused here. Runs after BOTH suspend phases and before type lowering. The
        // REFERENCE build is exempt for the same reason it needs no `newSuspendLambda` phase: RefBodySquash replaces
        // EVERY body there with the metadata-only throw, which is already the physical body of a declaration that
        // cannot be executed — and its kotlin.* type tokens are kept verbatim, so an authored `NotSupportedException
        // (kotlin.String)` would have no CLR ctor to bind.
        if (!_options.RefBuild)
            SuspendResidueLowering.ApplyAll(
                staged.Select(s => s.Root).ToList(), _options.StdlibMode == BuildStdlibMode.App);

        // Every source declaration was frozen before representation erasure, and every synthesized suspend declaration
        // must have copied that fact from its semantic source. Refuse an incomplete current BIR here; do not reconstruct
        // Kotlin meaning from the now-physical `suspendRet`.
        RoundtripMetadata.RequireSuspendResults(staged.Select(s => s.Root));

        // Suspend lowering has just introduced completion parameters and state-machine declarations. Compare
        // their final carrier types with referenced physical slots, not a fresh Kotlin construction against an
        // already-erased reference signature. The post-bridge rewrite below still covers newly authored bridges.
        FBoundStarProjectionErasure.RewriteLateTypes(
            staged.Select(s => s.Root).ToList(), localExistentialOwners, refs);

        // KOTLIN ERASURE-NARROWED OVERRIDE -> FINAL CLR METHODIMPL (#344 / #86 D3). The declaration-move half ran
        // early, but the bridge half must see the FINAL declarations: one logical suspend override becomes a public
        // Task member AND a continuation cold entry, and each is a distinct CLR slot. SuspendColdLowering carries the
        // source `overrides` proof onto both generated declarations (renaming it for the cold member), so this single
        // rule emits one exact bridge/MethodImpl per physical obligation without teaching ilemit suspend semantics.
        // Ref builds skip suspend lowering and normalize their logical declaration here; app and rt builds normalize
        // the final Task/cold shapes. Star views already exist, and all types are still in the Kotlin vocabulary.
        KotlinOverrideSlotBridge.ApplyAll(
            staged.Select(s => s.Root).ToList(), isValueFqn, refs, localTypeFqns, _options.RefBuild, genericRepresentations,
            covariantBridgedSlots, localExistentialOwners);

        // The final override bridge deliberately runs after the main F-bound/star rewrite because suspend lowering
        // can create additional physical slots. Project any Kotlin star types copied into those late declarations
        // through the already-allocated existential ABI; no new carrier or semantic/member lookup occurs here.
        FBoundStarProjectionErasure.RewriteLateTypes(
            staged.Select(s => s.Root).ToList(), localExistentialOwners, refs);

        // Non-generic `System.IComparable` bridge. Synthesize it at this final Kotlin-vocabulary boundary so its
        // forwarding call carries the target declaration's semantic return stamp. In particular, a legal
        // `compareTo(...): Nothing` is normalized at that bridge's construction boundary, before the physical Int32
        // slot could receive Nothing's later CLR object erasure.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged) ComparableBridgeSynthesis.Apply(stagedFile.Root,
                type => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValueFqn,
                    refs.PhysicalTypeNames, typeArg: true, nullableFrames: refs.NullableTypeFrames));

        // @PublishedApi is a Kotlin semantic annotation carried alongside the source `internal` visibility in BIR.
        // Resolve its effective CLR accessibility here, before ownership selects top-level versus Nested* metadata.
        PublishedApiTypeVisibilityLowering.ApplyAll(staged.Select(s => s.Root));

        // Materialize every remaining Kotlin lexical owner as explicit CLR TypeDef nesting only after all source,
        // inline, event-adapter, closure, and coroutine synthesis has finished adding declarations. This is the sole
        // BIR semanticOwner -> CIR nestedIn boundary.
        TypeOwnershipLowering.ApplyAll(staged.Select(s => s.Root).ToList());
        // Opaque raw-BIR carriers cross the assembly boundary after the representation decision above. Bind any local
        // nested type tokens to this producer's exact metadata identity now; readers never reconstruct that relation.
        OpaqueCarrierTypeBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs, declarationSemanticSignatures);
        // A synthesized closure/SAM class holds each capture in an INSTANCE FIELD, which the CLR refuses for a
        // byref-like (`ref struct`) type. ClosureSynthesis recorded those refusals rather than throwing, because the
        // cold suspend lowering above reconstructs a `suspendCoroutine { … }` block inline and PRUNES the class it
        // came from — its captures become ordinary locals of the enclosing frame, judged by the suspend storage
        // gate instead. Report now, over the classes that actually survived.
        ClosureSynthesis.AssertSurvivingCapturesLegal(staged.Select(s => s.Root));

        // PHASE 1.7 — GENERIC SELF INSTANTIATION (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object emits
        // its self instance accesses with the BARE type name (`dotkt_obj144`, no type args) -> ".NET method/type
        // not fully instantiated" at runtime. Derive the constructed self `dotkt_obj144[gp:T]` for those
        // executable instance accesses (kotc emits the FQN identity; bir2cir derives the CLR instantiation).
        if (!_options.RefBuild)
            GenericSelfInstantiation.ApplyAll(staged.Select(s => s.Root).ToList());

        // PHASE 1.9 — GENERIC DELEGATE INSTANTIATION (#191): a `var x by D<T>(…)` delegated property whose user
        // delegate is GENERIC gets its getValue/setValue dispatch emitted with the BARE delegate owner (`"D"`, no
        // type args) while the `$delegate` field/local carries the constructed `D<String>` — the open owner
        // mismatches the constructed receiver (BadImageFormatException / ilverify `found 'string' expected '!0'`).
        // Recover the receiver's instantiation (StaticType.Surface) and stamp the constructed owner. The
        // delegated-property analog of GenericSelfInstantiation; kotc names the type, bir2cir derives the CLR
        // instantiation. BEFORE BirTypeLowering (so `D<kotlin.String>` lowers consistently with the receiver).
        if (!_options.RefBuild)
            GenericDelegateInstantiation.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // GENERIC STATIC OWNER BINDING: bind the semantic G owner to the explicit non-generic carrier selected above
        // (or validated from a referenced DotKt assembly). No representative type argument is chosen.
        GenericStaticOwnerBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // Carry the exact IsVolatile representation of local and referenced fields into each CIR access. In
        // particular, an access through a constructed local generic owner cannot rely on FieldInfo identity at emit
        // time: anchoring the declaration builder produces a different object. CIR therefore states the prefix itself.
        VolatileFieldLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // Kotlin const fields become CLR literal fields. In the reference build the physical scalar/string slot also
        // carries its exact Kotlin declaration type, because a locally-declared kotlin.String/Int TypeDef is not a
        // legal Constant-table type. ilemit still receives only final physical facts in either mode.
        ConstFieldLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs, _options.RefBuild);

        // CONSTRAINED TYPE-PARAMETER RECEIVER, phase 1 of 2: kotc names a type-parameter receiver's classifier with
        // a BARE token (`fun <N : Node<N>> N.close()` -> `Node`). Close it from N's own lexical bound, which source
        // writes constructed, so the inherited-owner walk immediately below has a constructed type to substitute the
        // exact DECLARING owner into. The node stays a callInstance; only the token changes.
        if (!_options.RefBuild)
            ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(staged.Select(s => s.Root).ToList());

        // INHERITED GENERIC MEMBER OWNER BINDING: BIR keeps the Kotlin receiver owner (`Derived<T>.m`), while a CLR
        // MemberRef must name the exact CONSTRUCTED declaring owner (`Base<T>.m`). Resolve that hierarchy substitution
        // here, from local declarations + kotc's override facts, before type lowering. This removes a semantic inference
        // from ilemit and prevents open-generic member operands ("containing type is not fully instantiated"). Exact
        // signature/arity matches only; ambiguous overloads are never guessed. No library/member names are special.
        if (!_options.RefBuild)
            InheritedMemberOwnerBinding.ApplyAll(staged.Select(s => s.Root).ToList(), refs);

        // A local inherited call only acquires its exact declaring owner in the transition above. Re-apply the
        // nullable-generic use contract now: `Derived : Base<String>` reading `Base<T>.values: Array<T?>` must call
        // the physical `object[]` slot and state the checked `string[]` projection explicitly. This is also before
        // UnsafeAccessorLowering, so a protected/private inherited edge and its synthesized wrapper agree on the same
        // physical result instead of leaving ilemit an impossible `object[] -> string[]` stack edge.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged)
            {
                NullableTvErasureCallRealign.ApplyAfterInheritedOwnerBinding(
                    stagedFile.Root, nullableTvDeclRets, isValueFqn, refs);
                UncheckedGenericCastReturnErasure.ApplyAfterInheritedOwnerBinding(
                    stagedFile.Root, uncheckedGenericCastRets);
            }

        // CONSTRAINED TYPE-PARAMETER RECEIVER, phase 2 of 2: a member called on a receiver whose static type is a
        // type PARAMETER (`fun <T : Tagged> f(t: T) = t.tag()`) cannot be a plain `callvirt` — the stack holds a
        // `!!T`, not an interface reference, so ECMA-335 requires an address plus `constrained. !!T ; callvirt`.
        // Author that dispatch now that the walk above has named the exact constructed DECLARING owner: naming the
        // receiver's bound instead would emit a MemberRef on a type that merely inherits the member, which binds
        // locally only through a fake override and has nothing to bind to across an assembly boundary.
        if (!_options.RefBuild)
            ConstrainedTypeParameterReceiverBinding.ApplyAll(
                staged.Select(s => s.Root).ToList(), isValueFqn, refs, localExistentialOwners);

        // Safe-call and other receiver normalization can postpone a projected type-parameter call until constrained
        // dispatch is selected above. Its exact source constraint is now present again; bind that remaining foreign
        // projected owner through the same metadata-identity runtime lane as the early calls.
        if (!_options.RefBuild)
            ForeignStarProjectionBinding.ApplyLate(
                staged.Select(s => s.Root).ToList(), localExistentialOwners, refs);

        // dll2klib restores G<*> for Kotlin source analysis while the referenced DLL physically exposes
        // its compiler-generated existential carrier. Re-apply that exact referenced ABI to call signatures and directly initialized locals before
        // CLR type lowering; ilemit must never infer the hidden physical signature.
        if (!_options.RefBuild)
            foreach (var stagedFile in staged)
                ReferenceExistentialAbiBinding.Apply(stagedFile.Root, refs);

        // Every structural synthesizer has now run. Allocate the complete declaration/property set before any pass
        // consumes physical Property descriptors: BackingFieldRename distinguishes receiverless storage ownership by
        // the exact getSig/setSig written here, including on late String/CharSequence adapters. This allocator is the
        // sole forward Kotlin-property -> CLR-name projection; no consumer reconstructs identity from that name.
        KotlinPropertyAccessors.AllocateDeclarationsAndProperties(staged.Select(s => s.Root));

        // AUTO-PROPERTY BACKING-FIELD RENAME (#228): kotc names an accessor-routed property's storage with the KOTLIN
        // identity, so the emitted type carried a property AND a field of the same name (reflection consumers cannot
        // resolve the pair). Mint the CLR metadata name `<Name>k__BackingField` here — the layer that owns the
        // Kotlin-to-CLR representation — and rewrite every field read/write that addresses it. GLOBAL (a
        // `byref(obj.prop)` addresses a sibling file's backing field) and unconditional (ref/rt/app emit one shape);
        // last in the structural phase, so every synthesized body exists and owner tokens are still Kotlin FQNs.
        BackingFieldRename.ApplyAll(staged.Select(s => s.Root).ToList());

        // Kotlin lexical visibility remains unchanged even when its declaration and use land in different CLR
        // TypeDefs. Project each otherwise-illegal physical edge to a private caller-side [UnsafeAccessor] extern;
        // target members are never widened to internal/protectedInternal.
        if (_options.RefBuild)
            UnsafeAccessorLowering.DropFacts(staged.Select(s => s.Root).ToList());
        else
        {
            UnsafeAccessorLowering.ApplyAll(staged.Select(s => s.Root).ToList(), refs);
            // UnsafeAccessor creates holder TypeDefs and replaces restricted calls with wrapper calls. Index
            // those exact physical declarations, then flow the new edges through the nullable-generic boundary once.
            // The exact selected declaration stamps the wrapper with one-shot erasure ownership. Local declarations
            // contribute their in-memory carrier; referenced declarations contribute the compiler-authored metadata
            // indexed by declaration identity. Thus a lifted closure can state wrapper object[] -> captured
            // Array<R?> explicitly without treating every late object return as nullable-generic erasure.
            foreach (var stagedFile in staged)
            {
                NullableTvErasureCallRealign.CollectNewSyntheticTypes(stagedFile.Root, nullableTvDeclRets);
                NullableTvErasureCallRealign.CollectNewSyntheticMembers(stagedFile.Root, nullableTvDeclRets);
            }
            foreach (var stagedFile in staged)
                NullableTvErasureCallRealign.ApplyAfterUnsafeAccessorSynthesis(
                    stagedFile.Root, nullableTvDeclRets, isValueFqn, refs);
        }
        foreach (var stagedFile in staged)
            NullableTvErasureCallRealign.ConsumeNullableErasureOwnership(stagedFile.Root);
        FBoundStarProjectionErasure.RemoveTransientFacts(staged.Select(s => s.Root), preserveExactOuter: true);

        // Resolve the Kotlin `lateinitGet` failure path to an ordinary UPAE construction before constructor binding.
        // This is deliberately after UnsafeAccessorLowering, which can rebuild a private-field lateinitGet around a
        // byref load, so every surviving node carries one complete physical exception expression into CIR.
        LateinitGetLowering.ApplyAll(staged.Select(s => s.Root));

        // Normalize every local call/delegate descriptor before the module-wide local member binding below. Generic
        // delegate targets may carry a closed function shape while their declaration remains open; bir2cir owns the
        // declaration lookup and records that open signature explicitly for one-to-one emission.
        foreach (var stagedFile in staged)
            ClrMemberResolution.EnsurePlainCallDescriptors(stagedFile.Root);
        ClrMemberResolution.ResolveLocalDelegateTargets(staged.Select(s => s.Root), refs,
            localCarriers: localExistentialOwners);

        // Suspend/state-machine and bridge synthesis above can add classes after the initial source-declaration
        // inventory was collected. The final CLR-member stamping pass must see the complete emission-unit set so a
        // local `new` is never mistaken for a referenced type and reflected against the compile universe.
        var emittedLocalTypes = AnySlotRebind.CollectLocalTypes(staged.Select(s => s.Root))
            .Keys.ToHashSet(StringComparer.Ordinal);
        // Some generated declarations are representation templates for canonical TypeDefs emitted once by the
        // runtime stdlib.  The shared catalog is the single ownership registry used by bir2cir and ilemit; the exact
        // shipped type must also exist before an app can treat its template as external.
        var externalCanonicalTypes = (_options.StdlibMode == BuildStdlibMode.App
            ? staged.SelectMany(s => s.Root is JsonObject root && root["types"] is JsonArray types
                ? types.OfType<JsonObject>()
                : Enumerable.Empty<JsonObject>())
            .Where(type => (type["generated"] as JsonValue)?.TryGetValue<bool>(out var generated) == true && generated)
            .Select(type => (type["name"] as JsonValue)?.GetValue<string>())
            .Where(name => !string.IsNullOrEmpty(name)
                && ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(name)
                && refs.PhysicalTypeNamed(name) != null)
            : Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        // STAR-PROJECTION BOUND index (#2): the in-assembly generic type-param BOUNDS (`interface Key<E : Element>`
        // -> {Key: [Element]}), collected across ALL staged roots (a `Key<*>` use may live in a sibling file from Key's
        // declaration). Feeds StarProjectionBoundLowering so a `Key<object>` (kotc's star-projection erasure) is
        // repointed to `Key<Element>` for the stdlib's OWN Key; a REFERENCED Key resolves via refs.TvBound instead.
        var starProjBounds = StarProjectionBoundLowering.CollectTypeParamBounds(staged.Select(s => s.Root));
        // The validator's local declaration index and referenced generic-declaration cache are module invariants.
        // Prepare them once before per-file physical finishing; each root is checked only after its last BIR-space
        // type-argument rewrite below.
        var externalGenericConstraintValidation = ExternalGenericConstraintValidation.Prepare(
            staged.Select(file => file.Root).ToList(), refs, isValueFqn, localBasicEnums);
        // The early semantic index only prevents a local accessor call from being captured by an external ancestor.
        // Capture the actual post-allocation MethodDef names now, once, for exact call allocation below.
        var localPropertyAccessors = MemberCallSubstitution.CollectLocalPropertyAccessors(
            staged.Select(s => s.Root));
        // PHASE 2 — per-file type lowering onwards.
        var files = new List<CirFile>();
        var pendingOutput = new List<(BirFileDeclaration Root, string OutputName)>();
        // The fully-lowered roots, kept so the implementing-position half of the crossing refusal can be asked of the
        // WHOLE compilation at once: a Kotlin interface declared in one file may be the only path from a class in
        // another to the .NET declaration whose slot it cannot fill.
        var loweredRoots = new List<(JsonNode Root, string File)>();
        // Reference assemblies retain Kotlin type vocabulary, but their MethodDef names must match the runtime twin.
        // Derive the collision set from an isolated runtime-physical projection in every build mode.
        var declarationCollisionProjection = new List<JsonNode>();
        var finalLocalDeclarationIds = DeclarationIdentityBinding.CollectDeclarationIds(staged.Select(s => s.Root));
        var declarationCollisionAliases = DeclarationIdentityBinding.CollisionAliases(
            staged.Select(s => s.Root), refs.Aliases);
        // Cold entries, state machines and their capture fields are now materialized. Index those actual
        // declarations together with the source-to-slot relation carried by their allocating passes.
        var existentialReceiverMembers = ExistentialReceiverBinding.Collect(
            staged.Select(s => s.Root), physicalDeclarationSources);
        if (!_options.RefBuild)
            CollectionBclSlotSynthesis.RecordContracts(staged.Select(s => s.Root), refs);
        foreach (var (substituted, outputName) in staged)
        {
            // §11 CONTINUATION-ERASURE (bundle-6 bug #5 ROOT): make the coroutine ABI monomorphic on
            // kotlin.coroutines.Continuation<object>. Ordinary Continuation[X] surfaces -> Continuation[kotlin.Any],
            // while exact lexical declaring-owner operands retain their actual frame. The Result<X> protocol boundary
            // follows the same erased value contract (Option A: the
            // resumeWith method + its Result-construction call args). ALL builds (ref/rt agree), BEFORE type lowering
            // (kotlin.Any then lowers to object in rt/app, verbatim in ref). Un-blocks BlockOnSink/startCoroutine/
            // resumeWith dispatch (CLR interface variance does not lift value types; uniform erasure is the fix).
            ContinuationErasure.Apply(substituted, refs);
            // Exact enclosing receiver facts outlive the ordinary value projection through this final ABI erasure.
            FBoundStarProjectionErasure.RemoveTransientFacts(new[] { substituted });
            GenericDowncastRealignment.Apply(substituted, genericDowncastHierarchy);
            // GenericDowncastRealignment aligns a local's declared type with an erased
            // existential-carrier cast. Bind calls through that local to the exact synthesized
            // existential slot before CLR type lowering.
            ExistentialReceiverBinding.Apply(substituted, existentialReceiverMembers, refs);
            // SEQUENCE for-in dispatch (#37 m1 wave-2, cases/il-seqforin): a `for (x in seq)` over a Kotlin Sequence
            // lowers to `forEachInline` with a typed `IEnumerable<elem>::GetEnumerator` dispatch, but the anon Sequence
            // `sequence { .. }` returns is erased to `IEnumerable<object>` at runtime (its lifted class carries no type
            // param yet declares `IEnumerable<T>` over the enclosing method's T) -> the typed slot is absent
            // (EntryPointNotFound). Re-point such a forEachInline onto the variance-immune non-generic
            // `System.Collections.IEnumerable`/`IEnumerator` + an element cast. Non-ref; before type lowering (the src's
            // `kotlin.sequences.Sequence` FQN is still in the source vocabulary).
            if (!_options.RefBuild) SequenceForEachLowering.Apply(substituted);
            // All declaration shaping and captured Kotlin body metadata are complete. A reference assembly has
            // no executable implementation: remove its bodies before binding physical calls that will never be
            // emitted. Signatures, constraints, constructor delegation, inline/default payloads and attributes stay.
            if (_options.RefBuild) RefBodySquash.Squash(substituted);
            // Resolve an attributed callStatic to the referenced declaration while its Kotlin descriptor still
            // distinguishes shapes that share one CLR erasure (`T?` vs object, function-return `T?` vs object).
            // The selected declaration then follows the ordinary nullable/alias/type transform into physical CIR.
            // App-only: stdlib metadata/runtime builds own their kotlin.* facades in this assembly.
            ClrMemberResolution.EnsurePlainCallDescriptors(substituted);
            // Signature-shaping passes above operate on Kotlin-visible parameters and may rebuild `sig` from the
            // semantic declaration carrier. Re-append the already-materialized hidden nullable witnesses now, at the
            // final physical-binding boundary, so exact reference resolution sees the actual MethodDef signature.
            ReifiedNullabilityWitnessLowering.FinalizeCallSignatures(substituted);
            // Fail closed if a late materialization introduced an identity-bearing external call after the early
            // binding boundary; the same authoritative ID lookup applies and no erased overload search is permitted.
            DeclarationIdentityBinding.BindReferenced(substituted, refs, finalLocalDeclarationIds,
                preserveForScalarResolution: attributeTopLevelOwner);
            if (attributeTopLevelOwner)
                ClrMemberResolution.ResolveReferencedStaticCalls(
                    substituted, refs, emittedLocalTypes, externalCanonicalTypes,
                    finalLocalDeclarationIds, outputName);
            // DECL-position NRT byte collection (#37/#48): stamp `nullableFlags`/`retNullableFlags` from the SEMANTIC
            // `{t:nullable}` reference wrappers BEFORE BirTypeLowering strips them to bare types. Runs in ALL builds so
            // the ref.dll + rt.dll + app views of a signature's nullability agree (the scalar decl flags are retired).
            // Count only arguments retained by the selected physical head, preserving the source annotation
            // wrappers on those arguments. The reference build must query its own representation as well.
            // Capture complete source slots before reference-nullable stripping. The exact KotlinType carrier
            // owns nested and outer nullability; NRT flags alone cannot restore a projected nominal source type.
            valueRepresentations.RecordSourceTypes(substituted);
            DeclNullableFlags.Apply(substituted, isValueFqn,
                type => BirTypeLowering.AnnotationArguments(type, refs.Aliases, isValueFqn,
                    refs.PhysicalTypeNames, emittedLocalTypes, _options.RefBuild, refs.NullableTypeFrames),
                type => refs.Aliases.ContainsKey(type.Name));
            // COMPREHENSIVE reference-nullable strip (#37/#48): remove EVERY `{t:nullable,of:<reference>}` from the whole
            // tree — decl slots AND usage positions (owner generic type-args, argTypes/typeArgs, cast/expression types)
            // that LowerNode walks as generic JSON without routing through LowerType. ilemit's MapType asserts a value
            // inner, so a reference nullable in ANY position (a `Continuation<Any?>` owner arg crashed the ref emit) must
            // be gone. Runs AFTER DeclNullableFlags (byte walk already captured the semantic nullability) and BEFORE type
            // lowering (oracle unambiguous on kotlin.* names). Value/struct/enum `{t:nullable}` stays for ilemit;
            // Unit? remains until type lowering distinguishes its value ABI from Unit's void convention.
            ReferenceNullableStrip.Apply(substituted, isValueFqn);
            // #66 — RUNTIME stdlib build only: remove the `kotlin.Comparable` upper bound that substituted CLR
            // primitives cannot satisfy, plus `in` declaration-site variance that the CLR rejects for the realized
            // signature. kotc emits pure-Kotlin type params in every build (ref==rt BIR); this pass removes only the
            // constraints/variance incompatible with the runtime stdlib's substituted CLR shape. Runs
            // BEFORE BirTypeLowering (the constraint is still the pure `kotlin.Comparable` token here).
            if (_options.SubstituteStdlibBuild) StdlibSubstituteTypeParams.Apply(substituted);
            // STAR-PROJECTION BOUND LOWERING (#2): a `T<*>` on a self-ref-bounded generic (`Key<E : Element>`) that kotc
            // erased to `Key<object>` violates `E : Element` (illegal reified CLR instantiation). Repoint the objectish
            // arg to the type-param BOUND (`Key<Element>`), reading the constraint from the in-assembly declaration (its
            // self-build) or refs.TvBound (a referenced owner). ALL builds, BEFORE BirTypeLowering (still kotlin.Any /
            // dotted Kotlin FQNs here), so ref.dll + rt.dll + app agree on the corrected signature.
            StarProjectionBoundLowering.Apply(substituted, starProjBounds, refs);
            // A reference KLIB can express ordinary nominal generic bounds, but not ECMA's class/struct/new() flags;
            // projecting the implicit ValueType/Enum rows as Kotlin bounds is worse, because no Kotlin value inhabits
            // those CLR root classifiers. Validate that physical half against the authoritative reference metadata
            // after every BIR-space splice/retyping/star-bound rewrite has settled the constructed arguments and before
            // type lowering erases their Kotlin/value identities. Foreign declaration descriptors are deliberately not
            // use-site TypeSpecs and retain their own generic frames. The frontend remains the owner of nominal bounds.
            externalGenericConstraintValidation.Apply(substituted);
            // #29 ROUND-TRIP RECORD: before the type transform collapses a nested read-only `kotlin.collections.List/
            // Set/Collection` (Root V) to its invariant sibling `IList`/`ICollection` — colliding with the mutable
            // sibling's own alias and losing the Kotlin read-only-vs-mutable identity — stash the PRE-collapse Kotlin
            // type of each affected declaration position as an opaque string. Member slots use
            // [KotlinCollectionIdentity]; type edges and bounds merge into [KotlinSupertypes]. dll2klib therefore
            // restores `List` vs `MutableList` cross-module in both channels. APP builds only (the collapse is
            // non-ref; only an app-emitted library is dll2klib-re-consumed). Runs on kotlin.* names before lowering.
            if (attributeTopLevelOwner) CollectionIdentityRecord.RecordMemberSlots(substituted);
            // The type transform: lower the Kotlin type vocabulary into ilemit's CLR-codegen vocabulary, emitting a
            // BIR-SHAPED CIR (same node shape; only type strings change). No verbatim/envelope track. The ref.dll
            // @ClrTypeAlias index lowers EVERY CLR-bound type (collections/StringBuilder/Regex/... not just the
            // hardcoded primitives) wherever it appears as a type token. The struct-ness oracle drives the reference
            // `{t:nullable}` strip (a value `T?` stays `Nullable<T>`; a reference `T?` -> bare + the NRT byte above).
            // #305 §2.7 CHOKEPOINT — every pass that can retype a node has now run, and BirTypeLowering below STRIPS
            // `sty`, so this is the last point at which the stamp exists to be checked. A stale stamp surviving here
            // is a pass that changed a node's result type without carrying `sty` with it.
            CheckStySanity(outputName, substituted);
            KotlinHashStorageLowering.Complete(substituted);
            KotlinPropertyAccessors.AllocateAll(substituted, refs, localPropertyAccessors);
            var collisionProjection = substituted.DeepClone();
            var physicalProjection = BirTypeLowering.Lower(
                collisionProjection, refBuild: false, declarationCollisionAliases, isValueFqn, outputName,
                refs.PhysicalTypeNames, emittedLocalTypes, refs.NullableTypeFrames, valueRepresentations.InterfaceMarkers);
            FunctionSignatureIdentity.Complete(physicalProjection);
            declarationCollisionProjection.Add(physicalProjection);
            var lowered = BirTypeLowering.Lower(substituted, _options.RefBuild, refs.Aliases, isValueFqn, outputName,
                refs.PhysicalTypeNames, emittedLocalTypes, refs.NullableTypeFrames, valueRepresentations.InterfaceMarkers);
            // The erasure can collapse two Kotlin declarations onto ONE CLR signature, where only one of them can
            // ever be called and the other is unreachable. Checked HERE, on the lowered tree, because that is where
            // the physical signature is final: `T?` reaches `object` through this pass and `Any?` reaches it through
            // the reference-nullable strip, so nothing earlier sees the two meet. Refuses loudly, naming both source
            // signatures — a silent wrong binding is the one outcome a program with no valid lowering must not get.
            NullableGenericOverloadCollision.Check(lowered, outputName);
            loweredRoots.Add((lowered, outputName));
        }

        // #395: Kotlin declarations that were distinct before type lowering may now occupy one CLI signature.
        // Allocate their MethodDef names from the common runtime-physical projection and rewrite all local uses from
        // the frontend-selected declaration identity before any module-wide member binding consults declarations.
        var declarationSignatures = DeclarationSignatureIdentity.Plan(declarationCollisionProjection, refs);
        DeclarationSignatureIdentity.Apply(declarationCollisionProjection, declarationSignatures);
        DeclarationSignatureIdentity.Apply(loweredRoots.Select(s => s.Root), declarationSignatures);
        DeclarationSignatureIdentity.BindSelectedOverrides(loweredRoots.Select(s => s.Root), declarationSignatures,
            refs, isValueFqn);
        var declarationPhysicalNames = DeclarationIdentityBinding.AllocatePhysicalNames(
            declarationCollisionProjection, out var declarationSemanticCarrierIds);
        DeclarationIdentityBinding.ApplyLocal(
            loweredRoots.Select(s => s.Root), declarationPhysicalNames,
            declarationSemanticCarrierIds, declarationSemanticSignatures,
            physicalParameterIndices, refs);
        KotlinPropertyAccessors.FinalizePhysicalProperties(loweredRoots.Select(s => s.Root));

        // SAME-UNIT CONSTRUCTOR BINDING: resolve `new`, `this(...)`, and local `super(...)` to declaration indices
        // while every source file is visible together. This deliberately follows physical type lowering and its
        // collision refusal: an invalid pair of Kotlin constructors that erases to one CLR signature must receive the
        // existing actionable collision diagnostic, rather than being intercepted as an ambiguous local lookup.
        // ilemit receives only the winning declaration index and never selects by name/arity/assignability.
        ClrMemberResolution.ResolveLocalConstructors(loweredRoots.Select(s => s.Root));
        // Compare fully physical method returns: closing a generic R with Unit still returns a value,
        // whereas a declared Kotlin Unit return has lowered to void. Adapt the delegate, not the method.
        ClrMemberResolution.ResolveLocalDelegateTargets(loweredRoots.Select(s => s.Root), refs,
            physicalDelegates: true, localCarriers: localExistentialOwners);
        // A call may retain a local receiver owner while its declaration lives on an inherited referenced
        // interface.  Resolve that local/external boundary only after every physically-lowered local declaration
        // is visible, so a stale shipped copy of a local owner can never win.
        ClrMemberResolution.ResolveInheritedExternalCalls(loweredRoots.Select(s => s.Root), refs);
        // Some lowering stages synthesize or physically reshape calls after the early semantic-signature pass.
        // Re-run the same exact external binding on the final physical descriptors; the explicit local-type set
        // keeps every declaration emitted by this compilation on the local axis.
        foreach (var (lowered, outputName) in loweredRoots)
            ClrMemberResolution.ResolveReferencedStaticCalls(
                lowered, refs, emittedLocalTypes, externalCanonicalTypes,
                finalLocalDeclarationIds, outputName);

        // PHASE 3A — complete every representation synthesis module-wide before any pass snapshots the final type
        // graph. A derived type can precede its base in another file; resolving interface manifests inside this loop
        // would otherwise make their contents depend on source-file order.
        if (!_options.RefBuild)
            CollectionBclSlotSynthesis.ApplyAll(loweredRoots.Select(s => s.Root), refs,
                type => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValueFqn,
                    refs.PhysicalTypeNames, typeArg: true, nullableFrames: refs.NullableTypeFrames));
        foreach (var (lowered, _) in loweredRoots)
        {
            // `.size` (Count) on a STAR-PROJECTED / `Any`-erased collection receiver: StarProjectionLowering already
            // re-pointed the receiver `cast` at a non-generic BCL collection interface, but MemberCallSubstitution bound
            // Count to the GENERIC `IReadOnly*<object>.Count`, absent on a value-type-arg collection (`List<int>`)
            // -> EntryPointNotFound. Re-point such Count reads at the VARIANCE-IMMUNE non-generic
            // `System.Collections.ICollection.Count`. App build only; runs AFTER MemberCallSubstitution so Count is bound.
            if (attributeTopLevelOwner) StarProjectionCountLowering.Apply(lowered);
            // The READ-ONLY sibling of every mutable collection face this unit's types name. Kotlin's `MutableList<E>`
            // IS-A `List<E>`, but their lowered CLR faces (`IList<T>` / `IReadOnlyList<T>`) are unrelated interfaces,
            // so the read-only view is real only when the emitted type declares it. Runs AFTER the mutable faces are
            // final (CollectionBclSlotSynthesis adds the ICollection<E> face of a list implementer) and BEFORE the
            // interface-slot manifests below, so the sibling is an ordinary stated interface everywhere downstream.
            // All builds: the rule is keyed on the lowered BCL face, which the reference build (whose surface stays
            // Kotlin-faced) simply never has.
            ReadOnlyCollectionViewInterfaces.Apply(lowered);
        }

        if (_options.RefBuild)
            emittedLocalTypes.Add(KotlinMapStorageSynthesis.EmitReferenceSurface(
                loweredRoots.Select(s => s.Root).ToList()));
        else
            foreach (var carrier in KotlinMapStorageSynthesis.ApplyAll(
                         loweredRoots.Select(s => s.Root).ToList(), refs, _options.SubstituteStdlibBuild))
                emittedLocalTypes.Add(carrier);

        if (!_options.RefBuild)
            foreach (var carrier in ReadOnlyCollectionStorageSynthesis.ApplyAll(
                         loweredRoots.Select(s => s.Root).ToList(), refs))
                emittedLocalTypes.Add(carrier);

        // THE REVERSE ENUMERATOR BRIDGE (#139/#400): a class whose supertype graph reaches a BCL enumerable face owes
        // `IEnumerator<E> GetEnumerator()` and has only Kotlin's `iterator(): Iterator<E>`. Author the compiler-owned
        // adapter TypeDef and both GetEnumerator halves — declarations, bodies and exact MethodImpl descriptors — so
        // ilemit emits them one-to-one. Module-wide (the face can be inherited through a base declared in a sibling
        // file) and after the loop above, whose last passes can still state such a face. Non-ref: the reference
        // surface keeps the Kotlin collection faces, so nothing in it implements a BCL enumerable interface.
        if (!_options.RefBuild
            && ReverseEnumeratorBridgeSynthesis.ApplyAll(loweredRoots.Select(s => s.Root).ToList(), refs))
            // The adapter is declared by THIS emission unit, later than the initial inventory, so record it before
            // any member binding below can mistake a local construction for a reference to another assembly.
            emittedLocalTypes.Add(ReverseEnumeratorBridgeSynthesis.AdapterName);

        // Final member/value binding must share the complete emission-unit ownership inventory,
        // including late collection carriers. Canonical runtime templates remain owned by the stdlib.
        refs.SetLocalEmittedTypes(localTypeFqns.Concat(emittedLocalTypes)
            .Except(externalCanonicalTypes, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal));

        // Late synthesis above authors ordinary calls as part of new bridge bodies. Bind those calls only after every
        // root has reached its final representation, then resolve local-owner calls inherited from external bases.
        ClrMemberResolution.ResolveInheritedExternalCalls(loweredRoots.Select(s => s.Root), refs);
        foreach (var (lowered, outputName) in loweredRoots)
            ClrMemberResolution.ResolveReferencedStaticCalls(
                lowered, refs, emittedLocalTypes, externalCanonicalTypes,
                finalLocalDeclarationIds, outputName);

        // Declaration inheritance and MethodImpl ownership are physical CLR edges. Put their final TypeSpecs on the
        // same exact reflected identity so ilemit cannot visit one slot twice through an arity-free alias and an exact
        // owner. This runs after every pass that can add an interface or descriptor (including read-only collection
        // views and reverse-enumerator synthesis), but before ResolveInterfaceSlots snapshots those final edges into
        // ilemit's exact slot manifests. The reference build keeps Kotlin vocabulary and deliberately skips this
        // CIR-only normalization.
        if (!_options.RefBuild)
            foreach (var (lowered, _) in loweredRoots)
                ExactExternalDeclarationIdentity.Apply(lowered, refs);

        CapturedGenericInvocation.PrepareLocalDeclarations(loweredRoots.Select(file => file.Root),
            flattenMissingOwners: _options.SubstituteStdlibBuild);
        // PHASE 3B — metadata, exact external identities, and validation over the now-stable module graph.
        bool runtimeRepresentationTypes = false;
        foreach (var (lowered, outputName) in loweredRoots)
        {
            // ROUNDTRIP METADATA (#71 S2): GENERATE every [Kotlin*]/[Nullable]/[NullableContext] attribute as ordinary
            // CIR `attrs`/`retAttrs` entries (ilemit then only STAMPS them via its generic BuildCab path — no Kotlin
            // knowledge left in ilemit). Runs on the fully-lowered decls so the materialized facts (nullableFlags,
            // suspendFnType, inlineBir, mods, suspendBridge, readOnly) are all present. SKIPPED in the runtime build
            // (`!SubstituteStdlibBuild`) except for runtime linkage and representation-type associations. Placed after
            // the earlier reference-body squash, which preserves all captured metadata. The attribute-class DEFS
            // are emitted ONCE below (SynthDefsFile), not per-file.
            if (!_options.SubstituteStdlibBuild) RoundtripMetadata.Stamp(lowered);
            // Runtime builds retain declaration linkage and representation-type associations, not source slots.
            // Exact runtime MethodDefs are associated with their reference twins by declaration ID.
            else
            {
                RoundtripMetadata.StripRuntimeAttrs(lowered);
                runtimeRepresentationTypes |= RoundtripMetadata.HasRepresentationTypeAssociations(lowered);
            }
            // The Kotlin `suspend` modifier has no CLR meaning and every consumer of it is a bir2cir pass: the cold
            // lowering, and the [KotlinFunction(Suspend)] stamp just written (which is how a referenced module still
            // sees a suspend surface). CIR describes the physical graph, so the flag stops here — ilemit has no
            // coroutine semantics to apply to it, and IrSanity refuses one that survives.
            SuspendResidueLowering.DropModifier(lowered);
            // W1-S2 (#46): resolve every clrStatic/clrInstance/newClr against the ref.dll MLC. Internal
            // `resolvedMemberParams` carries matching inputs between bir2cir passes; Apply consumes it into the scalar
            // memberRef (+ `dispatch` on clrInstance). ilemit is a pure linker. Runs LAST — on
            // the fully-lowered tree — so owner/argTypes speak the CLR vocabulary the MLC resolves; unconditional so
            // RefBodySquash's `newClr NotImplementedException` is stamped too (its owner resolves off the BCL compile-refs).
            ClrMemberResolution.EnsurePlainCallDescriptors(lowered);
            ClrMemberResolution.Apply(lowered, refs, emittedLocalTypes);
            CapturedGenericInvocation.Apply(lowered, refs, emittedLocalTypes);
            // Exact member resolution above selected the authoritative MethodDef and temporarily carried its CLR-only
            // generic parameter facts. Validate the call's actual method arguments in the caller's lexical frame, then
            // consume that internal carrier before CIR serialization. Generic Kotlin properties are physical accessor
            // MethodDefs here and therefore follow the same rule as functions and extension methods.
            externalGenericConstraintValidation.ApplyResolvedMembers(lowered);
            // Property nodes reveal whether they denote an accessor or a public field only after CLR member
            // resolution. Convert the accessor subset on type-variable receivers to constrained calls now, retaining
            // the exact memberRef just resolved; fields keep their distinct load/store representation.
            ConstrainedTypeParameterReceiverBinding.ApplyResolvedProperties(new[] { lowered });
            ClrMemberResolution.ResolveWellKnown(lowered, refs);
        }

        var hasAssemblyAttributes = birRoots.OfType<JsonObject>().Any(root =>
            root["assemblyAttrs"] is JsonArray { Count: > 0 });
        // Every remaining owner consumes typed occurrences. Enter once before
        // interface-slot resolution, delegate construction and physical value
        // edges; no pass below returns the graph to DOM.
        var physicalRoots = loweredRoots.Select(file =>
            (Root: BirGraphDom.ReadBirFileDeclaration(file.Root), file.File)).ToArray();
        loweredRoots.Clear();
        var physicalDocuments = physicalRoots.Select(file => file.Root).ToArray();
        // Interface obligations resolve against the complete final declaration
        // graph, using the same owning records as the following consumers.
        ClrMemberResolution.ResolveInterfaceSlots(physicalDocuments, refs);
        foreach (var (root, file) in physicalRoots)
        {
            // All member and interface-slot resolution is complete module-wide.
            // Materialize each construction's final delegate and any required
            // adapter as owning typed declarations with exact member bindings.
            ClrMemberResolution.MaterializeDelegateSlots(root, refs, emittedLocalTypes, localExistentialOwners);
            ForeignStarProjectionBinding.DropDeclarationSignatures(root);
            // A saved foreign return is the resolver's declaration fact, not a
            // value to reconstruct from the caller-facing type or memberRef.
            ClrMemberResolution.CheckStamped(root, file);
            ForeignNullableGenericCrossing.Check(root, file);
        }
        // Implementing obligations are module-wide: an external slot may be
        // reached through a Kotlin interface in another input file.
        ForeignNullableGenericCrossing.CheckImplementedSlots(physicalRoots, refs);
        // Consume CLR attribute origin and select each external constructor from
        // its declaration, retaining the shared foreign-parameter validation.
        foreach (var (root, file) in physicalRoots) TypedAttributeBinding.Apply(root, refs, file);
        // Native import lowering owns the MethodDef/ImplMap descriptor. Attribute
        // identity and generic-crossing validation remain authoritative.
        foreach (var root in physicalDocuments) PInvokeLowering.Apply(root, refs, localBasicEnums);

        // BirTypeLowering's mutable/read-only collection faces are sibling CLR interfaces. Materialize every cast
        // required by the final value-flow graph only now, after every synthetic declaration and exact memberRef is
        // stable. ilemit then emits those ordinary CIR casts without recognizing the collection ABI. A metadata/ref
        // build retains declaration types and only consumes semantic comparisons in executable constructor remnants.
        var physicalExistentialOwners = localExistentialOwners.Values.ToHashSet(StringComparer.Ordinal);
        TypedPhysicalValueCoercion.ApplyAll(physicalDocuments,
            () => ClrMemberResolution.UnitSingletonRead(refs), isValueFqn, referenceBuild: _options.RefBuild,
            isReferenceOwner: owner => refs.ResolveNetType(owner.Name, owner.Args?.Length ?? 0)
                is { IsClass: true, IsValueType: false } native
                && native.FullName is not ("System.Object" or "System.ValueType" or "System.Enum"),
            isExistentialOwner: owner => physicalExistentialOwners.Contains(owner.Name)
                || refs.IsExistentialPhysicalOwner(owner.Name),
            isNativeReference: owner => refs.ResolveNetType(owner.Name, owner.Args?.Length ?? 0)
                is { IsValueType: false },
            referencedCarrier: (target, source) => refs.TryExistentialPhysicalOwner(target.Name, out var carrier)
                && carrier == source.Name, references: refs);
        foreach (var root in physicalDocuments) FunctionSignatureIdentity.Complete(root);

        // Final executable CIR admission: every call/conversion and suspend state machine is now physical.
        // Ordered operands cross return, loop-transfer and protected-region boundaries only at an empty stack.
        // This must follow suspend lowering: a temporary CLR address must never become a state-machine field.
        if (!_options.RefBuild)
        {
            var physicalResultType = TypedPhysicalValueCoercion.ResultTypeOracle(physicalDocuments);
            foreach (var root in physicalDocuments)
                TypedPhysicalOperandNormalization.Apply(root, isValueFqn, physicalResultType, refs);
        }

        // Every representation synthesis is now complete. Validate the exact MethodDef table that CIR will describe;
        // do not defer a generated/user collision to ilemit and do not invent a late name after calls are bound.
        DeclarationIdentityBinding.ValidateFinalPhysicalNames(physicalDocuments);
        foreach (var (root, _) in physicalRoots) TypedProtectedRegions.Apply(root, refs);

        // Canonical runtime synthetics were useful as module-wide representation templates, but an application does
        // not emit their TypeDefs. Remove those declarations from CIR so its local/external ownership is literal:
        // every surviving reference to the type names the runtime assembly and no duplicate local declaration exists.
        foreach (var (root, _) in physicalRoots)
            if (root.Types.HasValue)
                root.Types = root.Types.Value.Where(type => !externalCanonicalTypes.Contains(TypedCirProjection.TypeName(type))).ToArray();

        // SERIALIZATION IS THE LAST THING, and not the tail of the loop above. The check that just ran asks each
        // Kotlin body which slot it fills, and the answer is a pass-to-pass record on the declaration that must not
        // reach CIR — so the check consumes it, and it can only do that once every file has been lowered and read.
        // Writing a file's JSON inside the loop froze that record into the CIR of every file but the last.
        // Preserve the assembly-wide fixed-member contract independently of source-root elision below. A valid
        // alias-only compilation can have every lowered source root removed by IsEmptyCir; its synthetic CIR root is
        // then the only surviving carrier, and ilemit must still receive the complete fail-closed role table.
        var fixedMemberSource = physicalRoots.Select(file => file.Root.WellKnownRefs.OrDefault())
            .FirstOrDefault(table => table != null);
        var fixedMemberTable = fixedMemberSource?.Members;
        foreach (var (lowered, outputName) in physicalRoots)
        {
            // A file whose ENTIRE content was @ClrTypeAlias types (e.g. Primitives.kt, Comparable.kt) is now empty after
            // AliasHelperHoist dropped them — emit no CIR file for it (an empty file-class would be a pointless empty
            // static type in the assembly). Assembly declaration metadata also keeps a root alive; never skip in ref.
            if (!_options.RefBuild && IsEmptyCir(lowered)) continue;
            pendingOutput.Add((lowered, outputName));
        }

        // #220: both stdlib twins define the canonical wide-delegate family. This is a physical CLR declaration,
        // authored directly as CIR after all source-semantic passes have completed; it is not a Kotlin source type
        // and must not be fed back through those passes. The fixed synthetic file also makes the ownership visible in
        // captured CIR instead of letting ilemit manufacture an undeclared ABI from a build-mode switch.
        var physicalDefinitions = new List<CirFile>();
        if (_options.StdlibMode != BuildStdlibMode.App)
        {
            if (pendingOutput.Any(f => f.OutputName == CanonicalDelegateSynthesis.OutputName))
                throw new InvalidOperationException(
                    $"bir2cir: reserved synthetic CIR name '{CanonicalDelegateSynthesis.OutputName}' collides with an input file");
            var delegateDefinitions = CanonicalDelegateSynthesis.SynthDefsFile();
            if (fixedMemberTable != null) delegateDefinitions = delegateDefinitions with {
                WellKnownRefs = new Dictionary<string, MemberRefNode>(fixedMemberTable, StringComparer.Ordinal),
            };
            physicalDefinitions.Insert(0, new CirFile(CanonicalDelegateSynthesis.OutputName, delegateDefinitions));
        }

        // #71 S2: emit the embedded round-trip attribute-class defs ONCE per assembly, as a dedicated synthetic CIR
        // file (glob-sorted first via the `000-` prefix so its TypeDefs precede the user types, minimizing dump churn).
        // Alias declarations also survive in a stdlib runtime assembly; projection still excludes that assembly
        // by its explicit stdlib marker. Runtime twins also keep representation-type associations and rich-enum API maps.
        if (!_options.SubstituteStdlibBuild || runtimeRepresentationTypes || localRichEnums.Count != 0 || hasAssemblyAttributes)
        {
            const string synthName = "000-dotkt-roundtrip-attrs.cir.json";
            // A real source file must never map to the reserved synthetic name — the two CirFiles would clobber on disk
            // and every Kotlin stamp would then silently vanish (its attr class absent -> BuildCab skips).
            if (pendingOutput.Any(f => f.OutputName == synthName) || physicalDefinitions.Any(file => file.OutputName == synthName))
                throw new InvalidOperationException($"bir2cir: reserved synthetic CIR name '{synthName}' collides with an input file");
            var attributeDefinitions = RoundtripMetadata.SynthDefsFile(refs, runtimeOnly: _options.SubstituteStdlibBuild);
            if (fixedMemberTable != null) attributeDefinitions = attributeDefinitions with {
                WellKnownRefs = new Dictionary<string, MemberRefNode>(fixedMemberTable, StringComparer.Ordinal),
            };
            physicalDefinitions.Insert(0, new CirFile(synthName, attributeDefinitions));
        }

        // These declarations already own physical CIR. Include their names in
        // the module's annotation-admission set, without projecting them to DOM.
        files.AddRange(physicalDefinitions);
        var emitted = pendingOutput.SelectMany(file => file.Root.Types.OrEmpty()).Select(TypedCirProjection.TypeName)
            .Concat(physicalDefinitions.SelectMany(file => file.Model.Types).Select(type => type.Name))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var projection = new TypedCirProjection(emitted);
        var declarations = ClrTypeVariance.ApplyAll(pendingOutput.Select(file =>
            projection.ProjectCirFileDeclaration(file.Root)).ToArray(), refs);
        for (var index = 0; index < pendingOutput.Count; index++)
        {
            var declaration = declarations[index];
            if (_options.RefBuild) declaration = RefBodySquash.LowerLiteralPlaceholders(declaration);
            files.Add(new CirFile(pendingOutput[index].OutputName, declaration));
        }
        return files;
    }

    // A lowered CIR root that carries no types, no methods and no fields contributes nothing — its file-class would be
    // an empty static type. True once AliasHelperHoist has dropped a file whose sole content was @ClrTypeAlias types.
    static bool IsEmptyCir(BirFileDeclaration root)
    {
        return root.Types.OrEmpty().Length == 0 && root.Methods.OrEmpty().Length == 0
            && root.Fields.OrEmpty().Length == 0 && root.AssemblyAttrs.OrEmpty().Length == 0;
    }

    // #305: the spec §2.7 `sty` chokepoint, run on the FULLY-PASSED BIR of one file. `sty` is bir2cir-internal and
    // BirTypeLowering strips it, so the CIR gate below can never see one — the stamp has to be checked here, while it
    // still exists, or the invariant has no mechanical witness at all. Same bir-common checker, restricted to the one
    // check that is meaningful pre-lowering; the JsonDocument round-trip is what gives
    // the shared JsonElement-based checker a view of the JsonNode tree the passes work on.
    static void CheckStySanity(string outputName, JsonNode root)
    {
        // BOTH ends of the round trip carry the #147 depth bound: `BirJson.Writer` to write and `BirJson.DocOptions`
        // to read back. System.Text.Json defaults MaxDepth to 64 on each independently, and one Kotlin function with
        // deeply-nested inlined lambdas nests a method body past that — so a plain `ToJsonString()` here would crash
        // the sanity CHECKPOINT on exactly the input a checkpoint exists to survive.
        using var doc = JsonDocument.Parse(root.ToJsonString(BirJson.Writer), BirJson.DocOptions);
        try { IrSanity.CheckSourceStamps(new[] { doc.RootElement }); }
        catch (IrSanityException ex) { throw new InvalidOperationException($"{outputName}: {ex.Decl}: sanity: {ex.Message}"); }
    }

    // Validate the actual typed output graph, before its one boundary serialization.
    static void CheckCirSanity(IReadOnlyList<CirFile> files)
    {
        try { IrSanity.Check(files.Select(file => file.Model)); }
        catch (IrSanityException ex) { throw new InvalidOperationException($"{ex.Decl}: sanity: {ex.Message}"); }
    }

    void WriteCirFiles(IReadOnlyList<CirFile> files)
    {
        foreach (var file in files)
        {
            var name = _options.CirFormat == DotKt.Cir.CirFileFormat.MessagePack
                ? file.OutputName[..^".json".Length] + ".msgpack" : file.OutputName;
            DotKt.Cir.CirFileCodec.Write(Path.Combine(_options.OutDir, name), file.Model, _options.CirFormat);
        }
    }

    static string OutputNameFor(string inputPath)
    {
        var name = Path.GetFileName(inputPath);
        if (name.EndsWith(".bir.msgpack", StringComparison.Ordinal))
            return name[..^".bir.msgpack".Length] + ".cir.json";
        if (name.EndsWith(".bir.json", StringComparison.Ordinal))
            return name[..^".bir.json".Length] + ".cir.json";
        if (name.EndsWith(".json", StringComparison.Ordinal))
            return name[..^".json".Length] + ".cir.json";
        return name + ".cir.json";
    }
}

// The three semantic build modes, selected by the single `--build-stdlib` CLI flag (absent = an app build). These
// REPLACE the old env-var soup (the stdlib-build + substitute env flags): metadata = COMPILE-set/SUBSTITUTE-unset,
// runtime = COMPILE-set/SUBSTITUTE-set, app = COMPILE-unset. (The fourth raw env combination — COMPILE-unset yet
// SUBSTITUTE-set — was never a real mode; the flag makes it unrepresentable.)
enum BuildStdlibMode { App, Metadata, Runtime }

sealed record DriverOptions(string OutDir, IReadOnlyList<string> CompileReferences, IReadOnlyList<string> Inputs,
    BuildStdlibMode StdlibMode, bool ReflectionRestricted, DotKt.Cir.CirFileFormat CirFormat = DotKt.Cir.CirFileFormat.Json)
{
    // The pure-Kotlin REFERENCE stdlib surface (`--build-stdlib=metadata` -> DotKt.Private.Stdlib.dll) keeps kotlin.*
    // type tokens verbatim and squashes bodies to a throw; EVERY other invocation — the runtime stdlib build and all
    // app builds — lowers kotlin.* to the CLR vocabulary.
    public bool RefBuild => StdlibMode == BuildStdlibMode.Metadata;

    // The RUNTIME stdlib build (`--build-stdlib=runtime` -> DotKt.Stdlib.dll) — NOT an app build. Since #66 kotc emits
    // one substitute-independent BIR, the rt-only type-param drops (kotlin.Comparable bound / `in` variance) that kotc
    // used to do live here (StdlibSubstituteTypeParams). App builds keep those, substituting the Comparable bound to
    // System.IComparable — so this is the stdlib-runtime build ONLY. Runtime attribute stripping and every other
    // Kotlin-to-CLR representation decision are likewise completed in bir2cir before ilemit sees the CIR.
    public bool SubstituteStdlibBuild => StdlibMode == BuildStdlibMode.Runtime;

    public static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new UsageException("bir2cir: missing output directory or input files");

        var outDir = args[0];
        var refs = new List<string>();
        var inputs = new List<string>();
        var mode = BuildStdlibMode.App;
        var reflectionRestricted = false;
        var cirFormat = DotKt.Cir.CirFileFormat.Json;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--compile-refs" when i + 1 < args.Length:
                    refs.AddRange(ManagedReferenceCatalog.Split(args[++i]));
                    break;
                case "--compile-refs":
                    throw new UsageException("bir2cir: --compile-refs requires a semicolon-separated path list");
                case "--ref":
                    throw new UsageException("bir2cir: --ref was replaced by --compile-refs");
                case "--build-stdlib=metadata":
                    mode = BuildStdlibMode.Metadata;
                    break;
                case "--build-stdlib=runtime":
                    mode = BuildStdlibMode.Runtime;
                    break;
                case "--reflection-restricted":
                    reflectionRestricted = true;
                    break;
                case "--cir-format=json":
                    cirFormat = DotKt.Cir.CirFileFormat.Json;
                    break;
                case "--cir-format=msgpack":
                    cirFormat = DotKt.Cir.CirFileFormat.MessagePack;
                    break;
                default:
                    if (args[i].StartsWith("--build-stdlib", StringComparison.Ordinal))
                        throw new UsageException($"bir2cir: --build-stdlib requires 'metadata' or 'runtime' (got '{args[i]}')");
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new UsageException($"bir2cir: unknown option '{args[i]}'");
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (inputs.Count == 0)
            throw new UsageException("bir2cir: no BIR input files");
        return new DriverOptions(outDir, refs, inputs, mode, reflectionRestricted, cirFormat);
    }
}

sealed record BirFile(string Path, JsonNode Root, SuspendShapeAnalysis Suspend);
sealed record LoadedBirModule(IReadOnlyList<BirFile> Files, SourceDeclarationSignatures Signatures,
    SourceModuleIndex SourceIndex,
    GenericRepresentationPolicy GenericRepresentations, KotlinGenericScalarSlots.Projection ScalarSlots,
    ValueRepresentationLowering ValueRepresentations,
    IReadOnlyDictionary<string, NullableRepresentationFrame> RepresentationMethodFrames,
    DeclarationPlacement Placement, ModulePreparation Preparation);
sealed record ModulePreparation(TypedModuleInventory Inventory,
    IReadOnlyDictionary<(string Owner, string Event), TypeNode> ForwardedEvents,
    NullableTvErasureCallRealign.DeclIndex NullableDeclarations,
    UncheckedGenericCastReturnErasure.DeclIndex UncheckedReturns, EnumPreparation Enums,
    IReadOnlyList<SharedSyntheticSynthesis.CellStorage> SharedCells);
sealed record EnumPreparation(HashSet<string> Rich, HashSet<string> Basic, bool RuntimeLookup);
sealed record DeclarationPlacement(IReadOnlyDictionary<string, MemberCallSubstitution.LocalFactory> Factories,
    IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey, IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> Properties);
sealed record CirFile(string OutputName, DotKt.Cir.CirFileDeclaration Model);

static class JsonOptions
{
    // MaxDepth raised off the STJ default (64) so a deeply-nested-lambda CIR document serializes (#147).
    public static readonly JsonSerializerOptions Indented = new() {
        WriteIndented = true, MaxDepth = DotKt.Bir.BirJson.MaxDepth,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}

sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
