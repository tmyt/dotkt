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
                TypeNodeCarrierSelfTest.Run();
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
                AliasDeclarationTransitions.HeaderFactsSelfTest();
                DeclarationRename.SourceSelfTest();
                MemberStrip.SelfTestSource();
                InheritedDefaultFakeOverrideElision.SelfTestSource();
                DeclarationIdentityBinding.SelfTest();
                CurrentDeclarationBindingsTests.SelfTest();
                ValueRepresentationLowering.PlacementSourceSelfTest();
                FBoundStarProjectionErasure.CurrentDeclarationPreparationSelfTest();
                FBoundStarProjectionErasure.FieldAccessorsSourceSelfTest();
                FBoundStarProjectionErasure.MethodPreparationSourceSelfTest();
                FBoundStarProjectionErasure.NativeProofSourceTransferSelfTest();
                FBoundStarProjectionErasure.CarrierDemandSourceSelfTest();
                FBoundStarProjectionErasure.ProjectionContextSelfTest();
                FBoundStarProjectionErasure.AlignmentSourceSelfTest();
                FBoundStarProjectionErasure.ParameterProjectionSourceSelfTest();
                FBoundStarProjectionErasure.SynthesisSourceSelfTest();
                FBoundStarProjectionErasure.ReferenceContractsSourceSelfTest();
                FBoundStarProjectionErasure.ConstructionSourceSelfTest();
                OwnerConstrainedMethodLowering.ContinuationSourceSelfTest();
                FBoundStarProjectionErasure.ConstraintSurfacesSourceSelfTest();
                FBoundStarProjectionErasure.NormalizationSourceSelfTest();
                FBoundStarProjectionErasure.RewriteSourceSelfTest();
                FBoundStarProjectionErasure.LateTypesSourceSelfTest();
                GenericArrayStorageLowering.SourceTypeOperationSelfTest();
                ExistentialSharedCellAlignment.SelfTestSource();
                SourceDeclarationSignatures.SelfTest();
                DeclarationSignatureIdentity.SelfTest();
                ForeignStarProjectionBinding.SelfTest();
                ForeignStarProjectionBinding.SelfTestSource();
                ExactMutableViewDispatch.SelfTestSource();
                MemberCallSubstitution.StaticCollectionPreferenceSelfTest();
                MemberCallSubstitution.SourceDeclarationSnapshotsSelfTest();
                MemberCallSubstitution.FactoriesSourceSelfTest();
                MemberCallSubstitution.SourceCallsSelfTest();
                FunctionSignatureIdentity.SelfTest();
                FunctionValueRepresentation.SelfTestSourceFacts();
                BirNodeType.SelfTest();
                CapturedGenericInvocation.SelfTestSourceFacts();
                CapturedGenericInvocation.SelfTestPhysicalInvocation();
                SourceModuleIndex.SelfTest();
                InheritedClassInterfaceBridge.SourceSelfTest();
                InheritedClassInterfaceBridge.LateSourceSelfTest();
                CovariantInterfaceReturnBridge.SourceSelfTest();
                BirFileCodec.SelfTest();
                BirGraphMutationTests.SelfTest();
                ClrTypeVariance.SelfTest();
                PhysicalValueCoercion.SelfTest();
                TypedPhysicalValueCoercion.SelfTest();
                TypedPhysicalOperandNormalization.SelfTest();
                TypedPhysicalDelegateAdapter.SelfTest();
                ClrMemberResolution.SelfTestTypedDelegateSlots();
                ClrMemberResolution.SelfTestTypedInterfaceSlots();
                ClrMemberResolution.SelfTestTypedBaseConstructors();
                ClrMemberResolution.TypedExactCallSelfTest();
                ClrMemberResolution.DeclarationBindingsSelfTest();
                ClrMemberResolution.SourceDescriptorSelfTest();
                ClrMemberResolution.SourceLocalDelegateTargetsSelfTest();
                ClrMemberResolution.TypedGenericParameterFactsSelfTest();
                BirGraphNameFieldsTests.SelfTest();
                StringCharSequenceBridge.SelfTest();
                StringCharSequenceBridge.SelfTestSource();
                IteratorConsumerNormalization.SelfTestSource();
                CatchClauseWidening.SelfTestSource();
                CharSeqStringLowering.SelfTestSource();
                PrimitiveOperatorLowering.SelfTest();
                SourceGraphRewrite.SelfTest();
                ForInLowering.SelfTestTypedRangePrefix();
                ArrayConstructionLowering.SelfTestSource();
                CharCodeInvokeLowering.SelfTestSource();
                NetInteropBinding.TypedSelfTest();
                PropertyReferenceFunctionLowering.SelfTestSource();
                LocalStaticOwnerBinding.SelfTestSource();
                NullableTvErasureCallRealign.SelfTestSourceFixedSlots();
                NullableTvErasureCallRealign.SelfTestSourceUses();
                ValueSlotNullableWrite.SelfTestSource();
                NullableGenericErasure.SourceSelfTest();
                ClrMemberResolution.SourceSignatureSelfTest();
                EnumMemberBinding.SelfTestSource();
                AnySlotRebind.SelfTestSource();
                RoundtripMetadata.SourceSuspendResultSelfTest();
                InheritedMemberOwnerBinding.SourceProjectionSelfTest();
                InheritedMemberOwnerBinding.FinalSourceSelfTest();
                NullableTvErasureCallRealign.SelfTestInheritedSource();
                UncheckedGenericCastReturnErasure.SelfTestInheritedSource();
                ConstrainedTypeParameterReceiverBinding.SelfTestSourceDispatch();
                TypedExecutableFrames.SelfTest();
                ClosureSynthesis.TypedMaterializedSelfTest();
                SharedSyntheticSynthesis.SelfTest();
                DefaultArgSplice.SourcePreparationSelfTest();
                FaithfulHintRecognition.TypedSelfTest();
                KotlinPropertyAccessors.SelfTestAccessorSignatures();
                BackingFieldRename.SourceSelfTest();
                LateinitGetLowering.SourceSelfTest();
                LateinitInitializationLoweringTests.SelfTest();
                CompanionExtensionBinding.SelfTest();
                LexicalDeclarationIds.SelfTest();
                ClrEventSubscriptionBinding.SelfTest();
                ClrEventImplBinding.SelfTestSourceFacts();
                ClrEventImplBinding.SelfTestLocalEventRecipes();
                ClrEventImplBinding.SelfTestTypedImplementations();
                ClrEventImplBinding.SelfTestTypedEventSlots();
                ExistentialReceiverBinding.SourceSelfTest();
                GenericDowncastRealignment.SourceSelfTest();
                SequenceForEachLowering.SourceSelfTest();
                RefBodySquash.SourceSelfTest();
                StarProjectionBoundLowering.SourcePreparationSelfTest();
                ExternalGenericConstraintValidation.SourcePreparationSelfTest();
                CollectionBclSlotSynthesis.SourcePreparationSelfTest();
                ValueRepresentationLowering.SourceTypesSelfTest();
                DeclNullableFlags.SourceSelfTest();
                CollectionIdentityRecord.MemberSlotsSourceSelfTest();
                ReifiedNullabilityWitnessLowering.FinalSignatureSourceSelfTest();
                ClrMemberResolution.ReferencedSourceSelfTest();
                ReferenceNullableStrip.SourceSelfTest();
                StdlibSubstituteTypeParams.SourceSelfTest();
                StarProjectionBoundLowering.SourceSelfTest();
                ExternalGenericConstraintValidation.SourceSelfTest();
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
                OpaqueCarrierTypeBinding.SourceSelfTest();
                GenericRepresentationPolicy.SelfTest();
                GenericRepresentationPolicy.FieldRenameSourceSelfTest();
                GenericSelfInstantiation.SourceSelfTest();
                GenericDelegateInstantiation.SourceSelfTest();
                GenericStaticOwnerBinding.SourceSelfTest();
                VolatileFieldLowering.SourceSelfTest();
                ConstFieldLowering.SourceSelfTest();
                KotlinGenericScalarSlots.SourceScalarSelfTest();
                BirTypeLowering.SelfTestSlotReturns();
                BirTypeLowering.SelfTestFunctionPointers();
                UncheckedGenericCastReturnErasure.SelfTest();
                UnsafeAccessorLowering.SuperForwarderSelfTest();
                UnsafeAccessorLowering.FamilyAccessSelfTest();
                UnsafeAccessorLowering.SourceSelfTest();
                BirTypeLowering.SelfTestMethodImplMetadata();
                SupertypeGraph.SelfTestDeclarationIdentity();
                CollectionHelperBinding.SelfTest();
                SourceCollectionInputs.SelfTest();
                IntrinsicExtensionRepresentation.SelfTest();
                ReferenceExistentialAbiBinding.SelfTest();
                ReferenceExistentialAbiBinding.SourceSelfTest();
                ForeignStarProjectionBinding.SelfTestLateSource();
                KotlinCollectionSlotSynthesis.SelfTest();
                CollectionBclSlotSynthesis.SelfTest();
                KotlinOverrideSlotBridge.SelfTest();
                KotlinOverrideSlotBridge.SuspendSourceSelfTest();
                KotlinOverrideSlotBridge.FinalSourceSelfTest();
                KotlinOverrideSlotBridge.SourceParameterSnapshotSelfTest();
                CollectionIdentityRecord.SourceSelfTest();
                KotlinCollectionIdentitySynthesis.SourceSelfTest();
                FBoundStarProjectionErasure.ArraySourceFactsSelfTest();
                TypeOwnershipLowering.SelfTestSourceApplications();
                TypeOwnershipLowering.SelfTestLatePlacement();
                PublishedApiTypeVisibilityLowering.SelfTestSourceVisibility();
                ComparableBridgeSynthesis.SourceSelfTest();
                ContinuationErasure.SelfTest();
                ContinuationErasure.StrictFrontierSelfTest();
                ReferenceNullableStrip.FunctionSelfTest();
                StarProjectionBoundLowering.FunctionSelfTest();
                ClrMemberResolution.InheritedGenericResultSelfTest();
                ClrMemberResolution.FunctionSlotComparisonSelfTest();
                StringCharSequenceBridge.CallSignatureSelfTest();
                ClrMemberResolution.NullableSlotComparisonSelfTest();
                ClrMemberResolution.WellKnownTableSelfTest();
                OwnerConstrainedMethodLowering.SelfTest();
                ConstrainedTypeParameterReceiverBinding.AritySnapshotSelfTest();
                ConstrainedTypeParameterReceiverBinding.SelfTestSourceCloseOwners();
                FBoundStarProjectionErasure.SelfTestSourceProjectionNormalization();
                ConstrainedTypeParameterReceiverBinding.ResolvedPropertiesSelfTest();
                SourceOwnerClosureTests.Run();
                StdlibSubstituteTypeParams.SelfTest();
                FBoundStarProjectionErasure.ProjectionConstraintSelfTest();
                ComparableRepresentationLowering.SelfTest();
                ValueRepresentationLowering.SelfTest();
                ValueRepresentationLowering.SourceSlotsSelfTest();
                ValueRepresentationSourcePolicy.SelfTest();
                CallableVisibilityLowering.SelfTest();
                AliasVarianceRepresentation.SelfTest();
                ExactMutableViewDispatch.SelfTest();
                KotlinCollectionClassifierLowering.SelfTest();
                KotlinRuntimeTypeWitness.SourceSelfTest();
                StarProjectionLowering.SourceSelfTest();
                GenericArrayStorageLowering.SelfTest();
                ErasedElementValues.SelfTest();
                ExistentialCaptureAlignment.SourceSelfTest();
                ErasedElementValues.CaptureSourceSelfTest();
                RoundtripMetadata.RuntimeLinkageSelfTest();
                RoundtripMetadata.TypeHeaderSelfTest();
                RoundtripMetadata.SourceTypeAliasSelfTest();
                SuspendLiveness.SourceSelfTest();
                SuspendColdLowering.OperandPlanSourceSelfTest();
                SuspendColdLowering.DeclarationSourceSelfTest();
                SuspendColdLowering.SourceContinuationSelfTest();
                SuspendColdLowering.EngineSourceSelfTest();
                SuspendColdLowering.CallsSourceSelfTest();
                SuspendLambdaLowering.SourceSelfTest();
                SuspendResidueLowering.SourceSelfTest();
                ReferenceMetadataIndex.SelfTest();
                ReferenceMetadataIndex.PhysicalParameterFactsSelfTest();
                MetadataDeclaredMembersTests.SelfTest();
                ExternalGenericConstraintValidation.SelfTestResolved();
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
        var aliasTransitions = new AliasDeclarationTransitions();
        var sourceUseViews = new NullableTvErasureCallRealign.DeclIndex[captured.Length];
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
            SharedSyntheticSynthesis.Apply(source, refs, sharedCells,
                genericRepresentations.RegisterNativeStorageMaterialization);
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
        // Property-reference adaptation appends declarations. Capture the
        // consumers' original module clock before any file creates an adapter.
        // Function-typed field stores may target a sibling file's declaration.
        var propertyFunctionFields = PropertyReferenceFunctionLowering.CollectFieldSlots(captured);
        var localStatics = LocalStaticOwnerBinding.Collect(captured);
        var localReferenceTypes = AnySlotRebind.CollectLocalTypes(captured);
        var genericDowncastHierarchy = GenericDowncastRealignment.Collect(captured);
        var iterableHierarchy = SupertypeGraph.CollectSource(captured);
        var syntheticTypes = captured.Select(source =>
            NullableTvErasureCallRealign.CaptureNewSyntheticTypes(source, nullableDeclarations)).ToArray();
        for (var sourceIndexInModule = 0; sourceIndexInModule < captured.Length; sourceIndexInModule++)
        {
            var source = captured[sourceIndexInModule];
            PropertyReferenceFunctionLowering.Apply(source, propertyFunctionFields);
            LocalStaticOwnerBinding.Apply(source, localStatics);
            // Resolve physical value slots before erasure, retaining the
            // original native-slot/iterable/array/nullable-test dependency order.
            if (!referenceBuild)
            {
                ValueSlotNullableWrite.Apply(source, refs, isValueFqn);
                ValueElementIterableCoercion.Apply(source, isValueFqn, iterableHierarchy, refs);
            }
            ArrayNullableElemCanonicalization.Apply(source, isValueFqn);
            NullableIsInstMatch.Apply(source);
            if (!referenceBuild)
            {
                // Resolve inherited enum/Object dispatch before alias substitution,
                // then consume the remaining semantic equality and super intent.
                EnumMemberBinding.Apply(source, basicEnums);
                AnySlotRebind.Apply(source, localReferenceTypes, refs);
                KotlinEqualityLowering.Apply(source);
            }
            // Preserve the selected Kotlin overload and logical suspend result
            // before declaration erasure; function values consume erased slots.
            if (appBuild) ClrMemberResolution.CaptureReferencedStaticCallSignatures(source);
            RoundtripMetadata.FreezeSuspendResults(new[] { source });
            NullableGenericErasure.Apply(source, isValueFqn);
            captured[sourceIndexInModule] = (BirFileDeclaration)FunctionValueRepresentation.Apply(source, refs.IsByRefLikeFqn);
            if (!referenceBuild)
                ErasedElementValues.ApplyScalarCalls(captured[sourceIndexInModule], refs, isValueFqn,
                    genericRepresentations, call => scalarSlots.Result(call, refs));
        }
        var files = new List<BirFile>();
        var localEventDeclarations = new ClrEventImplBinding.LocalEvents();
        for (var index = 0; index < inputsWithSources.Count; index++)
        {
            var source = captured[index];
            // Admit each pristine synthetic declaration snapshot at its original
            // per-file clock. Later files must not become visible to this file's
            // initial nullable-use flow merely because preparation is module-wide.
            NullableTvErasureCallRealign.ApplyNewSyntheticTypes(syntheticTypes[index], nullableDeclarations);
            var sourceUses = nullableDeclarations.BindSourceVisibility();
            sourceUseViews[index] = sourceUses;
            NullableTvErasureCallRealign.ApplySourceUses(source, sourceUses, isValueFqn, refs);
            // Function constructions now state their consuming slots. Align
            // their owned targets, then reflow only when a declaration changed.
            if (uncheckedDeclarations.AlignDelegateTargets(source, isValueFqn))
                NullableTvErasureCallRealign.ApplyAfterDelegateSlotAlignment(source, sourceUses, isValueFqn, refs);
            // Keep unchecked generic results object-typed until a real typed
            // consumer; the captured body-Tv facts retain their exact identity.
            UncheckedGenericCastReturnErasure.Apply(source, uncheckedDeclarations);
            // Restore invariant collection call arguments from the original
            // selected declaration and this file's current owning body/frame.
            if (!referenceBuild) MapVarianceRealign.Apply(source, inventory.CalleeTypeParameters, refs);
            // Event delegate selection owns the pre-strip accessor recipe.
            // Capture it before retiring bound stubs; later event binding uses
            // this same authority even when that accessor no longer emits.
            localEventDeclarations.CaptureSource(source);
            if (!referenceBuild) MemberStrip.Apply(source, refs, RetireDeclaration);
            // Inherited interface implementations supply an existing slot, not
            // a second abstract declaration. Preserve their selected source row
            // before retiring the exact fake override occurrence.
            InheritedDefaultFakeOverrideElision.Apply(source, RetireDeclaration);
            RoundtripMetadata.FreezeTypeAliases(source);
        }
        // Finish every source-use pass before representation/name allocation
        // mutates declarations shared by the module's prepared authorities.
        // Preserve the original per-file alias -> selected ID -> override order.
        var charSeqReturnViews = new CharSeqStringLowering.CharSeqRetLambdas[captured.Length];
        for (var index = 0; index < captured.Length; index++)
        {
            var source = captured[index];
            // Alias implementations become static rule-3 helpers before call
            // substitution; reference builds retain the pure Kotlin surface.
            if (!referenceBuild)
                AliasHelperHoist.Apply(source, refs, genericRepresentations, aliasTransitions, RetireDeclaration);
            // Bind identity first: rebinding a reference-stub spelling after
            // CLR override allocation would undo that physical slot decision.
            DeclarationIdentityBinding.BindReferenced(source, refs, sourceIndex.DeclarationIds, deferUnknown: true);
            if (!referenceBuild) DeclarationRename.Apply(source, refs);
            // Preserve Kotlin classifier identity while selecting physical
            // membership and star-collection faces before call substitution.
            if (!referenceBuild)
            {
                KotlinRuntimeTypeWitness.LowerOperations(source, refs);
                KotlinCollectionClassifierLowering.Apply(source, refs);
            }
            if (appBuild) StarProjectionLowering.Apply(source, refs);
            // Event reads become ordinary handle constructions and callbacks.
            // Normalize only their newly authored, non-overlapping executable
            // roots; the owning file's original source-use flow stays untouched.
            ClrEventSubscriptionBinding.Apply(source, refs, forwardedEvents, out var materializedEventHandles);
            foreach (var materialized in materializedEventHandles)
                materialized.Slot.Replace(FunctionValueRepresentation.Apply(materialized.Slot.Value, refs.IsByRefLikeFqn));
            var sourceUses = sourceUseViews[index];
            NullableTvErasureCallRealign.ApplyMaterialized(materializedEventHandles, sourceUses, isValueFqn, refs);
            var eventClosures = ClosureSynthesis.ApplyMaterialized(source, materializedEventHandles, refs, scalarSlots.ProjectMethod);
            aliasTransitions.AdmitMaterializedTypes(source, eventClosures.AppendedTypes);
            if (DelegateTargetSlotAlignment.ApplyMaterialized(materializedEventHandles, eventClosures.AppendedTypes, isValueFqn))
                foreach (var owner in eventClosures.AppendedTypes)
                    NullableTvErasureCallRealign.ApplyAfterDelegateSlotAlignment(owner, sourceUses, isValueFqn, refs);
            // SharedSyntheticSynthesis already consumed the original classes.
            // These late callback classes must release the same transient facts.
            foreach (var owner in eventClosures.AppendedTypes) SharedSyntheticSynthesis.DropSyntheticTypeArgs(owner);
            // Resolve physical event delegates from the pre-strip source recipe.
            // Raise/subscription sites still bind at their later physical-owner clock.
            if (!referenceBuild) ClrEventImplBinding.BindImplementations(source, refs, localEventDeclarations);
            if (!referenceBuild) CompanionRepresentationLowering.AssertNoCompanionValues(source);
            // A user CharSequence implementation must retain the polymorphic
            // synthetic module-wide: snapshotting it would replace its authored
            // length/index behavior with toString(), and sealed System.String
            // cannot implement the interface contract.
            // Collapse only pure apps, while property identity is still explicit.
            CharSeqStringLowering.CharSeqRetLambdas charSeqReturns = null;
            if (!referenceBuild && appBuild && !inventory.HasUserCharSeqImplementation)
                CharSeqStringLowering.Apply(source, inventory.LocalTopLevelFunctions, out charSeqReturns);
            // The selected declaration owns the CLR binding. Local sibling
            // top-level calls stay owner-less; only app references are attributed.
            if (!referenceBuild)
                MemberCallSubstitution.Apply(source, refs, inventory.LocalTopLevelFunctions, appBuild, isValueFqn,
                    localPropertyDeclarations, genericRepresentations, localFactories, sourceIndex.DeclarationIds);
            charSeqReturnViews[index] = charSeqReturns;
        }

        localEventDeclarations.ReleaseSourceRecipes();
        // Complete every file's member substitution before these consumers
        // inspect sibling declarations. Keep each file's original nullable
        // visibility view, rather than recapturing the canonical module index.
        for (var index = 0; index < captured.Length; index++)
        {
            var source = captured[index];
            // Hidden witness arguments must not participate in the preceding
            // Kotlin-visible enum/array/collection recognition.
            ReifiedNullabilityWitnessLowering.MaterializeCallWitnesses(source);
            if (!referenceBuild)
            {
                // The selected referenced top-level declaration now has its
                // actual owner. Reuse the same return/argument flow authority.
                UncheckedGenericCastReturnErasure.ApplyReferenced(source, refs);
                NullableTvErasureCallRealign.ApplyAfterReferencedOwnerBinding(
                    source, sourceUseViews[index], isValueFqn, refs);
            }
            if (appBuild) IteratorConsumerNormalization.Apply(source);
            if (!referenceBuild)
            {
                // Preserve real CharSequence implementations in both runtime
                // and application builds; a sealed String needs an adapter.
                var bridge = StringCharSequenceBridge.Apply(source, refs, charSeqReturnViews[index]);
                var callbacks = ClosureSynthesis.ApplyMaterialized(
                    source, bridge.DelegateAdapters, refs, scalarSlots.ProjectMethod);
                // Both producers report their actual append order. Neither is
                // a new logical source declaration or a second semantic index.
                aliasTransitions.AdmitMaterializedTypes(source,
                    bridge.AppendedTypes.Concat(callbacks.AppendedTypes).ToArray());
                foreach (var owner in callbacks.AppendedTypes) SharedSyntheticSynthesis.DropSyntheticTypeArgs(owner);
                CatchClauseWidening.Apply(source);
            }
            sourceUseViews[index] = null;
            charSeqReturnViews[index] = null;
        }

        // This complete owning module still carries Kotlin classifier spelling
        // and independent generic frames. Capture source facts before physical
        // inner applications or erased element consumers rewrite those uses.
        if (!referenceBuild) ClrEventImplBinding.BindLocalSubscriptionsAll(captured);
        if (appBuild)
            foreach (var source in captured) CollectionIdentityRecord.RecordTypeEdges(source);
        if (!referenceBuild) KotlinCollectionIdentitySynthesis.ApplyAll(captured, refs);
        for (var index = 0; index < captured.Length; index++)
            BirTypeLowering.ValidatePointerVocabulary(captured[index], OutputNameFor(inputsWithSources[index].Path));
        FBoundStarProjectionErasure.RecordArrayTypeEdges(captured);
        var pendingOverrideParameters = KotlinOverrideSlotBridge.CaptureSourceParameters(captured);
        TypeOwnershipLowering.RecordNestedSourceTypes(captured, refs);
        TypeOwnershipLowering.ProjectInnerApplications(captured, refs);
        NullableTvErasureCallRealign.ProjectDeclarationApplications(nullableDeclarations);
        // Native raise slots use the newly flattened declaring-owner frame.
        if (!referenceBuild) ClrEventImplBinding.BindRaisesAll(captured, refs);
        if (!referenceBuild) ErasedElementValues.ApplyAll(captured, refs, isValueFqn, genericRepresentations);
        CapturedGenericInvocation.CaptureParameterWitnesses(captured);
        // Late physical locations are distinct from original source eligibility.
        // Close bound-owned receivers and normalize repeated variance before
        // FBound snapshots declarations and exact native proof requirements.
        genericRepresentations.RefreshPhysicalNativeLocations(captured);
        ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(captured);
        FBoundStarProjectionErasure.NormalizeRedundantProjections(captured, refs);
        var currentDeclarations = FBoundStarProjectionErasure.PrepareCurrentDeclarations(captured, genericRepresentations,
            out var currentDeclarationPreparation);
        var nativeProofs = FBoundStarProjectionErasure.PrepareNativeProofs(currentDeclarations, refs, genericRepresentations);
        var carrierSurface = FBoundStarProjectionErasure.PrepareCarrierSurface(
            currentDeclarationPreparation, nativeProofs, refs, genericRepresentations,
            (owner, appended) => pendingOverrideParameters.AdmitMaterializedMethods(owner, appended, currentDeclarations));

        // Surface preparation snapshots the original method parameter types.
        // Align every body's captured-inner values before projecting any source
        // parameter ABI, preserving the module-wide dependency order.
        foreach (var source in captured)
            FBoundStarProjectionErasure.AlignCapturedInnerBaseValuesSource(source, carrierSurface.Demand, refs);
        var carrierProjection = FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(
            carrierSurface.Demand, refs, aliasTransitions);
        FBoundStarProjectionErasure.ProjectParameterDeclarations(captured, carrierProjection, scalarSlots.Parameters);

        var carrierRealization = new FBoundStarProjectionErasure.CarrierRealizationContext(
            carrierSurface, carrierProjection, refs, carrierSurface.Demand.ReservedNames,
            (owner, appended) => pendingOverrideParameters.AdmitMaterializedMethods(owner, appended, currentDeclarations),
            (file, type) => {
                aliasTransitions.AdmitMaterializedTypes(file, new[] { type });
                genericRepresentations.AdmitMaterializedNativeTypes(file, new[] { type });
            });
        FBoundStarProjectionErasure.RealizeCarriersSource(captured, carrierRealization, sharedCells,
            (type, actual) => valueRepresentations.ComponentPredicate(type, actual),
            (file, appended) => {
                aliasTransitions.AdmitMaterializedTypes(file, appended);
                genericRepresentations.AdmitMaterializedNativeTypes(file, appended);
            });

        var localExistentialOwners = carrierRealization.Owners.Values.Where(owner => owner.Needed)
            .ToDictionary(owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal);
        ExistentialCaptureAlignment.ApplyAll(captured, localExistentialOwners, refs, isValueFqn, genericRepresentations,
            (owner, field, position, arity, storage) => NullableTvErasureCallRealign.RebindGeneratedCapture(
                nullableDeclarations, owner, field, position, arity, storage));
        void BindBridgeMethods(IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> appended)
        {
            foreach (var method in appended)
            {
                currentDeclarations.GetSource(method);
                foreach (var implementation in method.ClrInterfaceImpls.OrEmpty()) currentDeclarations.GetSource(implementation);
            }
        }
        void AdmitBridgeMethods(IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> appended)
        {
            BindBridgeMethods(owner, appended);
            pendingOverrideParameters.AdmitMaterializedMethods(owner, appended, currentDeclarations);
        }
        InheritedClassInterfaceBridge.ApplyAll(captured, AdmitBridgeMethods);
        var sourceCovariantSlots = CovariantInterfaceReturnBridge.ApplyAll(captured, refs, isValueFqn,
            genericRepresentations, pendingOverrideParameters.ReadSourceParameters(), (file, owner, method) => {
                AdmitBridgeMethods(owner, new[] { method });
                if (referenceBuild) return;
                var appended = StringCharSequenceBridge.ApplyMaterializedReturns(file, method);
                foreach (var type in appended)
                {
                    currentDeclarations.GetSource(type);
                    foreach (var constructor in type.Ctors.OrEmpty()) currentDeclarations.GetSource(constructor);
                    foreach (var member in type.Methods.OrEmpty()) currentDeclarations.GetSource(member);
                }
                aliasTransitions.AdmitMaterializedTypes(file, appended);
                genericRepresentations.AdmitMaterializedNativeTypes(file, appended);
            });
        // Keep the original slot-owner clock on the owning graph. Covariant
        // slots suppress duplicate suspend bridges using the same actual handles.
        KotlinOverrideSlotBridge.PrepareSuspendValueBridges(captured, isValueFqn, refs,
            inventory.EmittedTypeFqns, referenceBuild, genericRepresentations,
            sourceCovariantSlots, pendingOverrideParameters.ReadSourceParameters(),
            valuePolicy.HasClassifier,
            (file, owner, method, descriptor) => {
                if (method != null) AdmitBridgeMethods(owner, new[] { method });
                if (descriptor != null) currentDeclarations.GetSource(descriptor);
            });
        pendingOverrideParameters.Complete();
        if (!referenceBuild)
            KotlinCollectionSlotSynthesis.ApplyAll(captured, refs, isValueFqn, valueRepresentations, BindBridgeMethods);
        valueRepresentations.SynthesizeSlots(captured, isValueFqn, (owner, methods, properties) => {
            currentDeclarations.RetireSourceMembers(owner, methods, properties);
            foreach (var method in methods) RetireDeclaration(method);
        }, BindBridgeMethods);
        ConstructedMemberReturnSubstitution.ApplyAll(captured);

        var suspendTransitions = new SuspendColdLowering.SourcePipelineTransitions(captured,
            currentDeclarations, nativeProofs, genericRepresentations, aliasTransitions,
            RetireDeclaration, retiredDeclarations);
        var suspendModule = new SuspendColdLowering.SuspendModuleContext(captured, refs,
            inventory.EmittedTypeFqns, appBuild, localExistentialOwners, isValueFqn, signatures,
            scalarSlots.Parameters, carrierRealization.Declarations.PhysicalDeclarationSources,
            suspendTransitions);
        // Named Cold also runs in reference builds: the selected id|cold
        // MethodDef must exist in the reference twin before bodies are squashed.
        // Lambda bodies need no reference-build state-machine materialization.
        SuspendColdLowering.ApplyNamedSource(suspendModule);
        if (!referenceBuild) SuspendLambdaLowering.ApplyAll(suspendModule);

        // Retained stdlib suspend declarations have an explicit physical throw
        // body; app declarations must already have their cold/Task shapes.
        // Retire discarded executable forests while their actual owners are live.
        if (!referenceBuild)
            SuspendResidueLowering.ApplyAll(captured, appBuild, suspendTransitions.RetireDiscardedSubtree);
        RoundtripMetadata.RequireSuspendResults(captured);

        // Compare completed suspend signatures against the existing carrier ABI.
        // This current physical view never re-captures source eligibility/demand.
        FBoundStarProjectionErasure.RewriteLateTypesSource(captured, carrierRealization, aliasTransitions);
        KotlinOverrideSlotBridge.ApplyAll(captured, isValueFqn, refs, inventory.EmittedTypeFqns,
            referenceBuild, genericRepresentations, sourceCovariantSlots, localExistentialOwners,
            valuePolicy.HasClassifier, (file, owner, method, descriptor) => {
                if (method != null) suspendTransitions.PublishMethods(file, owner, new[] { method });
                if (descriptor != null) currentDeclarations.GetSource(descriptor);
            });
        // Project star-bearing slots copied by final bridges through the same ABI.
        var finalCarrierTypes = FBoundStarProjectionErasure.RewriteLateTypesSource(
            captured, carrierRealization, aliasTransitions);

        // A bridge is a new declaration. Rehoming a lifted method below is an
        // owning-edge MOVE of the same declaration, never a birth/retirement.
        if (!referenceBuild)
            ComparableBridgeSynthesis.ApplyAll(captured,
                type => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValueFqn,
                    refs.PhysicalTypeNames, typeArg: true, nullableFrames: refs.NullableTypeFrames),
                (file, owner, method) => suspendTransitions.PublishMethods(file, owner, new[] { method }));
        PublishedApiTypeVisibilityLowering.ApplyAll(captured);
        TypeOwnershipLowering.ApplyAll(captured, suspendTransitions.MoveMethods);
        OpaqueCarrierTypeBinding.ApplyAll(captured, refs, signatures);
        // Intrinsic reconstruction may have pruned a refused class completely.
        // Consume its existing synthesis verdict, never classify its fields again.
        ClosureSynthesis.AssertSurvivingCapturesLegal(captured);

        // These use-site owners consume the complete, finally placed generic
        // frame. They do not materialize or reselect declarations.
        if (!referenceBuild)
        {
            GenericSelfInstantiation.ApplyAll(captured);
            GenericDelegateInstantiation.ApplyAll(captured);
        }
        GenericStaticOwnerBinding.ApplyAll(captured, refs);
        VolatileFieldLowering.ApplyAll(captured, refs);
        ConstFieldLowering.ApplyAll(captured, refs, referenceBuild);

        if (!referenceBuild)
        {
            ConstrainedTypeParameterReceiverBinding.CloseOpenOwners(captured);
            InheritedMemberOwnerBinding.ApplyAll(captured, refs);
            foreach (var source in captured)
            {
                NullableTvErasureCallRealign.ApplyAfterInheritedOwnerBinding(
                    source, nullableDeclarations, isValueFqn, refs);
                UncheckedGenericCastReturnErasure.ApplyAfterInheritedOwnerBinding(source, uncheckedDeclarations);
            }
            ConstrainedTypeParameterReceiverBinding.ApplyAll(captured, isValueFqn, refs, localExistentialOwners);
            var lateProjection = FBoundStarProjectionErasure.CarrierProjectionContext.ForCurrentSource(
                carrierRealization, finalCarrierTypes, aliasTransitions);
            ForeignStarProjectionBinding.ContinueWithProjection((type, shape) =>
                lateProjection.Rewrite(type, preserveArrayShape: shape));
            ForeignStarProjectionBinding.ApplyLate(captured, localExistentialOwners, refs,
                (file, appended) => suspendTransitions.PublishTypes(file, appended),
                appended => {
                    if (appended.Count == 0) return;
                    finalCarrierTypes = FBoundStarProjectionErasure.RefreshCurrentPhysicalTypesSource(captured, carrierRealization);
                    lateProjection = FBoundStarProjectionErasure.CarrierProjectionContext.ForCurrentSource(
                        carrierRealization, finalCarrierTypes, aliasTransitions);
                    FBoundStarProjectionErasure.ProjectMaterializedTypesSource(
                        appended, carrierRealization, finalCarrierTypes, lateProjection);
                });
            foreach (var source in captured) ReferenceExistentialAbiBinding.Apply(source, refs);
        }

        // Allocate the complete property surface before storage naming and
        // lexical access select their exact declaration descriptors.
        KotlinPropertyAccessors.AllocateDeclarationsAndProperties(captured);
        var backingRenames = BackingFieldRename.ApplyAll(captured);
        genericRepresentations.ApplyFieldRenames(backingRenames);
        NullableTvErasureCallRealign.ApplyFieldRenames(nullableDeclarations, backingRenames);
        if (referenceBuild) UnsafeAccessorLowering.DropFacts(captured);
        else
        {
            UnsafeAccessorLowering.ApplyAll(captured, refs, suspendTransitions);
            foreach (var source in captured)
            {
                NullableTvErasureCallRealign.CollectNewSyntheticTypes(source, nullableDeclarations);
                NullableTvErasureCallRealign.CollectNewSyntheticMembers(source, nullableDeclarations);
            }
            foreach (var source in captured)
                NullableTvErasureCallRealign.ApplyAfterUnsafeAccessorSynthesis(source, nullableDeclarations, isValueFqn, refs);
        }
        foreach (var source in captured) NullableTvErasureCallRealign.ConsumeNullableErasureOwnership(source);
        finalCarrierTypes = FBoundStarProjectionErasure.RefreshCurrentPhysicalTypesSource(captured, carrierRealization);
        // UnsafeAccessor can rebuild the private storage read as a byref load.
        // Only then author its ordinary failure construction, without new types.
        LateinitGetLowering.ApplyAll(captured);

        // Resolve the current local declaration descriptors before taking the
        // module-wide inventories. These are physical snapshots at this clock,
        // not a second capture of source eligibility or carrier demand.
        foreach (var source in captured) ClrMemberResolution.EnsurePlainCallDescriptors(source);
        ClrMemberResolution.ResolveLocalDelegateTargetsSource(captured, refs,
            localCarriers: localExistentialOwners);
        var starProjectionBounds = StarProjectionBoundLowering.CollectTypeParamBounds(captured);
        var physicalPreparation = new PhysicalAbiPreparation(
            AnySlotRebind.CollectLocalTypes(captured).Keys.ToHashSet(StringComparer.Ordinal),
            (appBuild ? captured.SelectMany(file => file.Types.OrEmpty())
                .Where(type => type.Generated.OrDefault()).Select(type => type.Name.OrDefault())
                .Where(name => !string.IsNullOrEmpty(name)
                    && ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(name)
                    && refs.PhysicalTypeNamed(name) != null) : Enumerable.Empty<string>())
                .ToHashSet(StringComparer.Ordinal),
            ExternalGenericConstraintValidation.Prepare(captured, refs, isValueFqn, enums.Basic),
            MemberCallSubstitution.CollectLocalPropertyAccessors(captured),
            new SourceModuleIndex(captured, refs.Aliases));
        var existentialReceiverMembers = ExistentialReceiverBinding.Collect(captured,
            carrierRealization.Declarations.PhysicalDeclarationSources);
        if (!referenceBuild) CollectionBclSlotSynthesis.RecordContracts(captured, refs);
        for (var sourcePosition = 0; sourcePosition < captured.Length; sourcePosition++)
        {
            var source = captured[sourcePosition];
            // The coroutine ABI is monomorphic except for exact lexical and
            // enclosing receiver frames. Consume outer facts only after erasure.
            ContinuationErasure.Apply(source, refs);
            ContinuationErasure.ConsumeExactOuterSource(source);
            GenericDowncastRealignment.Apply(source, genericDowncastHierarchy);
            ExistentialReceiverBinding.Apply(source, existentialReceiverMembers, refs);
            if (!referenceBuild) SequenceForEachLowering.Apply(source);
            // Body retirement must precede projection while actual registered
            // owners remain attached. Signatures and opaque metadata survive.
            if (referenceBuild) RefBodySquash.Squash(source, suspendTransitions.RetireDiscardedSubtree);

            // Finish exact referenced selection while Kotlin descriptors still
            // distinguish declarations with the same physical erasure. Hidden
            // witnesses complete the already selected declaration signature.
            ClrMemberResolution.EnsurePlainCallDescriptors(source);
            ReifiedNullabilityWitnessLowering.FinalizeCallSignatures(source);
            DeclarationIdentityBinding.BindReferenced(source, refs,
                physicalPreparation.Declarations.DeclarationIds, preserveForScalarResolution: appBuild);
            if (appBuild)
                ClrMemberResolution.ResolveReferencedStaticCalls(source, refs,
                    physicalPreparation.EmittedLocalTypes, physicalPreparation.ExternalCanonicalTypes,
                    physicalPreparation.Declarations.DeclarationIds, OutputNameFor(inputsWithSources[sourcePosition].Path));

            // Record source identity and NRT before stripping reference-nullable
            // wrappers. Nullable Unit retains its distinct value-returning ABI.
            valueRepresentations.RecordSourceTypes(source);
            DeclNullableFlags.Apply(source, isValueFqn,
                type => BirTypeLowering.AnnotationArguments(type, refs.Aliases, isValueFqn,
                    refs.PhysicalTypeNames, physicalPreparation.EmittedLocalTypes, referenceBuild, refs.NullableTypeFrames),
                type => refs.Aliases.ContainsKey(type.Name));
            ReferenceNullableStrip.Apply(source, isValueFqn);
            if (substituteStdlibBuild) StdlibSubstituteTypeParams.Apply(source);
            StarProjectionBoundLowering.Apply(source, starProjectionBounds, refs);
            // Reuse the pre-erasure Prepared/cache: foreign declarations retain
            // their own generic frames, unlike constructed use-site TypeSpecs.
            physicalPreparation.GenericConstraints.Apply(source);
            if (appBuild) CollectionIdentityRecord.RecordMemberSlots(source);
        }

        for (var index = 0; index < captured.Length; index++)
        {
            var source = captured[index];
            captured[index] = null;
            // The remaining middle owners still use DOM. This is the single
            // continuing projection, after slots, placement, exact generic uses,
            // storage, lexical access, exact signatures and constraints. Retired facts
            // use the same observer.
            var root = source.ToDom(suspendTransitions.Projection.ObserveProjection);
            files.Add(new BirFile(
                inputsWithSources[index].Path,
                root,
                inputsWithSources[index].Suspend));
        }

        var continuingRoots = files.Select(file => file.Root).ToArray();
        suspendTransitions.Projection.ProjectRetiredForests();
        var retiredDom = suspendTransitions.Projection.RetiredMethods;
        currentDeclarations.BindProjected();
        genericRepresentations.BindDomDeclarations(continuingRoots, aliasTransitions, suspendTransitions.Projection);
        valueRepresentations.BindDomDeclarations(continuingRoots, retiredDom, suspendTransitions.Projection);
        // Its last call consumer ran on owning BIR. Validate the original
        // declaration continuation, then let this source-only ledger retire.
        _ = UncheckedGenericCastReturnErasure.BindDomDeclarations(uncheckedDeclarations,
            continuingRoots.Cast<JsonObject>().ToArray(), retiredDom, aliasTransitions, suspendTransitions.Projection);
        var carrierContinuation = FBoundStarProjectionErasure.BindRealizationContinuation(
            carrierRealization, finalCarrierTypes);
        aliasTransitions.Release();
        retiredDeclarations.Clear();
        suspendTransitions.Release();
        suspendModule.ReleaseSourceDeclarations();
        return new LoadedBirModule(files, signatures, sourceIndex,
            genericRepresentations, scalarSlots, valueRepresentations, representationMethodFrames,
            new ModulePreparation(inventory, enums,
                carrierContinuation, physicalPreparation));
    }

    List<CirFile> TransformFiles(LoadedBirModule module, ReferenceMetadataIndex refs)
    {
        _enumRuntimeLookup |= module.Preparation.Enums.RuntimeLookup;
        var birFiles = module.Files;
        var birRoots = birFiles.Select(b => b.Root).ToList();
        var declarationSemanticSignatures = module.Signatures;
        var valueRepresentations = module.ValueRepresentations;
        KotlinRuntimeTypeWitness.ValueRepresentations = valueRepresentations;
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
        // The type FQNs DECLARED in this compilation (every input file's own `types`). SuspendColdLowering
        // uses it to decide whether the cold-core base (kotlin.coroutines.clr.internal.ContinuationImpl) is
        // a LOCAL type (rt-stdlib self-build -> bare base + local slot override) or a REFERENCED one
        // (app build -> clr: base + pendingOverrideOwner linkage).
        var localTypeFqns = module.Preparation.Inventory.EmittedTypeFqns;
        // App-only physical interop stages remain separate from runtime/metadata
        // self-builds; source call attribution already ran in the owning prefix.
        var attributeTopLevelOwner = _options.StdlibMode == BuildStdlibMode.App;

        // Rich-enum declarations require their API metadata even in a runtime
        // stdlib build; the typed prefix has already resolved their uses.
        var localRichEnums = module.Preparation.Enums.Rich;
        // Later enum metadata and reflection indexing use the same module-wide
        // native-enum inventory already consumed by typed dispatch binding.
        var localBasicEnums = module.Preparation.Enums.Basic;

        // Call-boundary materialization has completed in the owning prefix.
        // The remaining type/slot/suspend owners consume this module-wide set.
        var staged = new List<(JsonNode Root, string OutputName)>();
        foreach (var bir in birFiles)
            staged.Add((bir.Root, OutputNameFor(bir.Path)));

        // Continue the owning prefix's selected carrier facts;
        // do not reselect them after the frontier.
        var localExistentialOwners = module.Preparation.Carriers.LocalOwners;

        // Suspend residue, logical results and final override slots have already
        // been resolved module-wide on the owning graph before the DOM frontier.

        // Comparable slots, effective visibility, nesting and opaque carriers
        // were resolved in that order before the owning projection.

        // Placed generic self/delegate/static owners, volatile ordering and CLR
        // literal fields were resolved before the owning projection.

        // Inherited owners, their erased call boundaries, constrained dispatch,
        // late foreign adapters and referenced existential ABI were resolved on
        // owning BIR. The remaining structural owners consume those exact facts.

        // Native transport facts survive until LoadBirInputs' strict address
        // handoff. Their consumers have finished; no source facts are recaptured.
        FBoundStarProjectionErasure.RemoveTransientFacts(staged.Select(s => s.Root));
        var physicalPreparation = module.Preparation.PhysicalAbi;
        var emittedLocalTypes = physicalPreparation.EmittedLocalTypes;
        var externalCanonicalTypes = physicalPreparation.ExternalCanonicalTypes;
        var externalGenericConstraintValidation = physicalPreparation.GenericConstraints;
        var localPropertyAccessors = physicalPreparation.PropertyAccessors;
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
        var finalLocalDeclarationIds = physicalPreparation.Declarations.DeclarationIds;
        var declarationCollisionAliases = physicalPreparation.Declarations.CollisionAliases;
        foreach (var (substituted, outputName) in staged)
        {
            // Exact call signatures, source metadata, reference nullability,
            // generic constraints and member collection identity are finished
            // on owning BIR. Physical property/type allocation continues here.
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
            localCarriers: localExistentialOwners);
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

        var capturedInvocations = CapturedGenericInvocation.PrepareLocalDeclarations(loweredRoots.Select(file => file.Root),
            flattenMissingOwners: _options.SubstituteStdlibBuild);
        // PHASE 3B — metadata, exact external identities, and validation over the now-stable module graph.
        bool runtimeRepresentationTypes = false;
        foreach (var (lowered, outputName) in loweredRoots)
        {
            // ROUNDTRIP METADATA (#71 S2): grant exact declaration/slot obligations and transfer metadata-only
            // facts here. CompleteMetadata encodes them at the typed seam as CIR attrs/retAttrs emitted by ilemit's
            // generic BuildCab path, with no Kotlin knowledge there. Materialized facts (nullableFlags,
            // suspendFnType, inlineBir, mods, suspendBridge, readOnly) are all present. SKIPPED in the runtime build
            // (`SubstituteStdlibBuild`) except for runtime linkage and representation-type associations. Placed after
            // the earlier reference-body squash, which preserves all captured metadata. The attribute-class DEFS
            // are emitted ONCE below (SynthDefsFile), not per-file.
            if (!_options.SubstituteStdlibBuild) RoundtripMetadata.PrepareMetadata(lowered, runtimeMode: false);
            // Runtime builds retain declaration linkage and representation-type associations, not source slots.
            // Exact runtime MethodDefs are associated with their reference twins by declaration ID.
            else
            {
                RoundtripMetadata.PrepareMetadata(lowered, runtimeMode: true);
            }
            // The Kotlin `suspend` modifier has no CLR meaning and every consumer of it is a bir2cir pass: the cold
            // lowering, and the pending [KotlinFunction(Suspend)] obligation (which is how a referenced module still
            // sees a suspend surface). DropModifier transfers that exact scalar before removing it — ilemit has no
            // coroutine semantics to apply to it, and IrSanity refuses one that survives.
            SuspendResidueLowering.DropModifier(lowered);
            // W1-S2 (#46): resolve every clrStatic/clrInstance/newClr against the ref.dll MLC. Internal
            // `resolvedMemberParams` carries matching inputs between bir2cir passes; Apply consumes it into the scalar
            // memberRef (+ `dispatch` on clrInstance). ilemit is a pure linker. Runs LAST — on
            // the fully-lowered tree — so owner/argTypes speak the CLR vocabulary the MLC resolves; unconditional so
            // RefBodySquash's `newClr NotImplementedException` is stamped too (its owner resolves off the BCL compile-refs).
            ClrMemberResolution.EnsurePlainCallDescriptors(lowered);
            ClrMemberResolution.Apply(lowered, refs, emittedLocalTypes);
        }

        var hasAssemblyAttributes = birRoots.OfType<JsonObject>().Any(root =>
            root["assemblyAttrs"] is JsonArray { Count: > 0 });
        // Every remaining owner consumes typed occurrences. Enter once before
        // interface-slot resolution, delegate construction and physical value
        // edges; no pass below returns the graph to DOM.
        var physicalRoots = loweredRoots.Select(file =>
            (Root: BirGraphDom.ReadBirFileDeclaration(file.Root), file.File)).ToArray();
        var physicalDocuments = physicalRoots.Select(file => file.Root).ToArray();
        // Resolve declaration operands on the continuing physical records before
        // invocation adaptation can append methods. The ordinary operation walk
        // has already bound its own call sites; it does not consume these edits.
        foreach (var root in physicalDocuments)
        {
            ClrMemberResolution.ApplyDeclarationBindings(root, refs, emittedLocalTypes);
            ClrMemberResolution.ResolveBaseConstructors(root, refs, emittedLocalTypes);
        }
        // Retain the original prepared declaration membership and exact source
        // anchors across this one-way frontier. The captured ID keys already
        // own selection; pending metadata and allocated names are not a new
        // declaration inventory. Both invocation and receiver materialization
        // now consume these same owning typed records before dispatch metadata.
        var physicalCapturedInvocations = CapturedGenericInvocation.BindPhysicalDeclarations(
            capturedInvocations, loweredRoots.Select(file => file.Root).ToArray(), physicalDocuments);
        loweredRoots.Clear();
        foreach (var root in physicalDocuments)
            physicalCapturedInvocations.Apply(root, refs, emittedLocalTypes);
        // Final resolution distinguished actual accessors from public fields.
        // Consume their receiver-dispatch facts on the same owning typed graph;
        // retain each exact MemberRef and each resolved file's own arity snapshot.
        foreach (var root in physicalDocuments)
            ConstrainedTypeParameterReceiverBinding.ApplyResolvedProperties(root);
        // Fixed BCL operands depend only on the reference universe. Author their
        // complete physical role table directly on the continuing typed roots.
        foreach (var root in physicalDocuments) ClrMemberResolution.ResolveWellKnown(root, refs);
        // Exact member resolution selected each MethodDef's CLR-only generic
        // constraints. Consume those facts on the same owning typed occurrences
        // before interface/delegate materialization; retain the prepared local
        // declarations and caller frames rather than rebuilding their authority.
        foreach (var root in physicalDocuments)
            externalGenericConstraintValidation.ApplyResolvedMembers(root);
        // Only declarations marked at the original metadata phase own a header
        // obligation. Later synthesized types do not acquire source metadata;
        // pending source facts are opaque to the intervening physical binders.
        foreach (var root in physicalDocuments)
        {
            RoundtripMetadata.CompleteMetadata(root, _options.SubstituteStdlibBuild);
            if (_options.SubstituteStdlibBuild)
                runtimeRepresentationTypes |= RoundtripMetadata.HasRepresentationTypeAssociations(root);
        }
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
    ModulePreparation Preparation);
sealed record ModulePreparation(TypedModuleInventory Inventory, EnumPreparation Enums,
    FBoundStarProjectionErasure.CarrierRealizationContinuation Carriers,
    PhysicalAbiPreparation PhysicalAbi);
sealed record PhysicalAbiPreparation(HashSet<string> EmittedLocalTypes,
    HashSet<string> ExternalCanonicalTypes,
    ExternalGenericConstraintValidation.Prepared GenericConstraints,
    IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
        IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> PropertyAccessors,
    SourceModuleIndex Declarations);
sealed record EnumPreparation(HashSet<string> Rich, HashSet<string> Basic, bool RuntimeLookup);
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
