using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    static SourceDeclarationProjection SourcePolicyCustody(params BirFileDeclaration[] roots)
    {
        var custody = new SourceDeclarationProjection(roots, new CurrentDeclarationBindings(roots));
        custody.SealSourceContinuations();
        return custody;
    }

    static void SourcePolicySelfTest()
    {
        var variable = new TypeNode.Tv("type", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        BirAttribute Export() => new() { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") };
        var exported = new BirFieldDeclaration { Name = "exported", Type = variable, Attrs = new[] { Export() } };
        var addressed = new BirFieldDeclaration { Name = "addressed", Type = variable };
        var ordinary = new BirFieldDeclaration { Name = "ordinary", Type = variable };
        var first = new BirFieldDeclaration { Name = "slot", Static = true, Type = text,
            CompanionReceiver = new TypeNode.Oblivious(new TypeNode.Fqn("First")), Attrs = new[] { Export() } };
        var second = new BirFieldDeclaration { Name = "slot", Static = true, Type = text,
            CompanionReceiver = new TypeNode.Fqn("Second", new TypeNode[] { text }) };
        var third = new BirFieldDeclaration { Name = "slot", Static = true, Type = text,
            CompanionReceiver = new TypeNode.Fqn("Third") };
        var fieldUse = new BirFieldGet { OwnerType = new TypeNode.Fqn("SourceFrame"), Name = "addressed", Recv = new BirThis() };
        var companionUse = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("SourcePolicy"), Name = "slot",
            CompanionReceiver = new TypeNode.Nullable(new TypeNode.Fqn("Second")) };
        var cellUse = new BirFieldGet { OwnerType = new TypeNode.Fqn("SourceCell"), Name = "v", Recv = new BirLocal { Name = "cell" } };
        var cell = new BirRefTypeDeclaration { Name = "SourceCell", Elem = text, TypeParams = Array.Empty<BirTypeParameter>() };
        var source = new BirClassDefinition { Name = "SourceFrame", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = new[] { exported, addressed, ordinary } };
        var generated = new BirClassDefinition { Name = "GeneratedFrame", Kind = "class", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var root = new BirFileDeclaration { FileClass = "SourcePolicy", Types = new IBirTypeDeclaration[] { source, generated },
            Fields = new[] { first, second, third }, RefTypes = new[] { cell }, Methods = new[] {
                new BirFunctionDeclaration { Name = "pass", Params = Array.Empty<BirParameter>(),
                    Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { new BirExpressionStatement {
                        Expr = new BirCallStatic { Method = "consume", OwnerType = new TypeNode.Fqn("Native"),
                            ShapeTypes = new TypeNode[] { new TypeNode.ByRef(variable), new TypeNode.ByRef(text), new TypeNode.ByRef(text) },
                            Args = new IBirExpression[] { fieldUse, new BirByRefOf { Inner = companionUse }, cellUse } },
                    } } },
            } };
        var policy = CreateSource(new[] { root }, new Dictionary<string, string>(), null);
        if (!exported._nativeLocation.OrDefault() || !addressed._nativeLocation.OrDefault()
            || ordinary._nativeLocation.IsPresent || !first._nativeLocation.OrDefault()
            || !second._nativeLocation.OrDefault() || third._nativeLocation.IsPresent
            || !cell._nativeLocation.OrDefault() || !fieldUse._nativeFieldAddress.OrDefault()
            || !companionUse._nativeFieldAddress.OrDefault() || !cellUse._nativeFieldAddress.OrDefault())
            throw new InvalidOperationException("Owning source policy changed native storage selection or companion field identity");
        var sourceApplication = new TypeNode.Fqn("SourceFrame", new TypeNode[] { text });
        var generatedApplication = new TypeNode.Fqn("GeneratedFrame", new TypeNode[] { text });
        if (!policy.OwnsValueSlots("SourceFrame") || !policy.OwnsValueSlots("SourceCell")
            || policy.ProjectArgumentHead(sourceApplication, true, null) is not TypeNode.Fqn { Args: [TypeNode.Star] }
            || policy.ProjectArgumentHead(generatedApplication, true, null) != generatedApplication)
            throw new InvalidOperationException("Owning source policy changed original generic eligibility");
        policy.PrepareSourceLocations(new[] { root });
        // The selected source cell becomes real storage before the same owning
        // handoff; its native role must move to the actual generated field.
        BirFieldDeclaration materializedCell = null;
        SharedSyntheticSynthesis.Apply(root, ReferenceMetadataIndex.Build(Array.Empty<string>()),
            materializedStorage: (origin, physical) => {
                if (!ReferenceEquals(origin, cell))
                    throw new InvalidOperationException("Ref-cell materialization did not report its exact source occurrence");
                materializedCell = physical;
                policy.RegisterNativeStorageMaterialization(origin, physical);
            });
        var actualCell = root.Types.Value.OfType<BirClassDefinition>().Single(type => type.Name.OrDefault() == "SourceCell");
        if (materializedCell == null || !ReferenceEquals(actualCell.Fields.Value[0], materializedCell)
            || !ReferenceEquals(policy._nativeStorageMaterializations[cell], materializedCell))
            throw new InvalidOperationException("Ref-cell materialization did not retain the actual authored storage field");
        var duplicateMaterialization = false;
        try { policy.RegisterNativeStorageMaterialization(cell, materializedCell); }
        catch (InvalidOperationException) { duplicateMaterialization = true; }
        if (!duplicateMaterialization || policy._nativeStorageMaterializations.Count != 1)
            throw new InvalidOperationException("Selected native source storage accepted a second materialization notification");
        var lateField = new BirFieldDeclaration { Name = "late", Type = text, Attrs = new[] { Export() } };
        var lateOwner = new BirClassDefinition { Kind = "class", Name = "LatePhysical", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = new[] { lateField } };
        root.Types = root.Types.Value.Append(lateOwner).ToArray();
        policy.RegisterCurrentPhysicalOwners(new[] { "LatePhysical" }, new Dictionary<string, string>());
        policy.RefreshPhysicalNativeLocations(new[] { root });
        var custody = SourcePolicyCustody(root);
        policy.CompleteSourceContinuation(new[] { root }, null, custody);
        var lateApplication = new TypeNode.Fqn("LatePhysical", new[] { text });
        if (root.RefTypes.IsPresent || policy._nativeStorageMaterializations.Count != 0
            || !ReferenceEquals(actualCell.Fields.Value[0], materializedCell)
            || !policy.IsNativeField(materializedCell) || !materializedCell._nativeLocation.OrDefault()
            || !policy.IsNativeField(addressed) || !policy.IsNativeField(lateField)
            || !policy.OwnsValueSlots("LatePhysical") || policy.ProjectArgumentHead(lateApplication, true, null) != lateApplication)
            throw new InvalidOperationException("Native storage completion lost an actual materialized field or changed fixed source eligibility");
        var refused = false;
        try { policy.CompleteSourceContinuation(new[] { root }, null, custody); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Native source custody completed twice");
        custody.Release();
        Console.WriteLine("[source generic policy] self-test OK (literal native selection, companion/ref-cell storage, exact typed custody, fixed eligibility)");
        NativePlacementSelfTest();
    }

    static void NativePlacementSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var valueType = new TypeNode.Fqn("Payload", new TypeNode[] { text });
        var field = new BirFieldDeclaration { Name = "value", Static = true, Type = valueType,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var source = new BirClassDefinition { Name = "Source", Kind = "class", Fields = new[] { field },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var payload = new BirClassDefinition { Name = "Payload", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = new[] {
                new BirFieldDeclaration { Name = "item", Type = new TypeNode.Tv("type", 0) },
            } };
        var use = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Source"), Name = "value", Sty = valueType };
        var read = new BirFunctionDeclaration { Name = "read", Static = true, Params = Array.Empty<BirParameter>(), Ret = valueType,
            Body = new IBirStatement[] { new BirReturn { Value = use } } };
        var root = new BirFileDeclaration { FileClass = "Placement", Types = new IBirTypeDeclaration[] { source, payload }, Methods = new[] { read } };
        var policy = CreateSource(new[] { root }, new Dictionary<string, string>(), null);
        var transitions = new AliasDeclarationTransitions();
        transitions.Record(root, root.Types.Value, root.Types.Value, Array.Empty<AliasDeclarationTransitions.Replacement>());
        var carrier = new BirClassDefinition { Name = "PhysicalStorage", Kind = "class", Generated = true,
            Fields = new[] { field } };
        field.Name = "physicalValue";
        source.Fields = Array.Empty<BirFieldDeclaration>();
        root.Types = new IBirTypeDeclaration[] { source, payload, carrier };
        transitions.AdmitMaterializedTypes(root, new[] { carrier });
        policy.PrepareSourceLocations(new[] { root });
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var declarations = FBoundStarProjectionErasure.PrepareCurrentDeclarations(new[] { root }, policy, out var preparation);
        var proofs = FBoundStarProjectionErasure.PrepareNativeProofs(declarations, refs, policy);
        var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, proofs, refs, policy);
        var realization = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
            FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs,
            surface.Demand.ReservedNames, materializedType: (owner, type) => transitions.AdmitMaterializedTypes(owner, new[] { type }));
        FBoundStarProjectionErasure.RealizeCarriersSource(new[] { root }, realization,
            Array.Empty<SharedSyntheticSynthesis.CellStorage>(), (_, _) => null,
            (owner, types) => transitions.AdmitMaterializedTypes(owner, types));
        var custody = SourcePolicyCustody(root);
        policy.CompleteSourceContinuation(new[] { root }, transitions, custody);
        if (!policy.IsNativeField(field)
            || !policy.IsNativeFieldUse("staticField", new TypeNode.Fqn("PhysicalStorage"), "physicalValue", null)
            || !policy.IsNativeFieldUse("staticField", new TypeNode.Fqn("Source"), "value", null)
            || policy.ProjectArgumentHead(new TypeNode.Fqn("Source", new[] { text }), true, null)
                is not TypeNode.Fqn { Args: [TypeNode.Star] }
            || policy.ProjectArgumentHead(new TypeNode.Fqn("PhysicalStorage", new[] { text }), true, null)
                is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] })
            throw new InvalidOperationException("Native physical placement lost selected identity or recomputed source eligibility");
        _ = FBoundStarProjectionErasure.CompleteRealization(realization);
        if (use.Sty.OrDefault() is not TypeNode.Fqn { Name: "Payload", Args: [TypeNode.Fqn { Name: "kotlin.String" }] }
            || !TypeNodeDataEquality.Equal(field.Type.Value, valueType))
            throw new InvalidOperationException("Native source-owner use lost its closed field witness before late physical binding");
        custody.Release();
        transitions.Release();

        foreach (var removeDeclaration in new[] { false, true })
        {
            var required = new BirFieldDeclaration { Name = "required", Type = text, Attrs = field.Attrs };
            var requiredRoot = new BirFileDeclaration { FileClass = "Required", Fields = new[] { required } };
            var requiredPolicy = CreateSource(new[] { requiredRoot }, new Dictionary<string, string>(), null);
            requiredPolicy.PrepareSourceLocations(new[] { requiredRoot });
            if (removeDeclaration) requiredRoot.Fields = Array.Empty<BirFieldDeclaration>();
            else required._nativeLocation = default;
            var requiredCustody = SourcePolicyCustody(requiredRoot);
            var rejected = false;
            try { requiredPolicy.CompleteSourceContinuation(new[] { requiredRoot }, null, requiredCustody); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Native binding accepted an absent or unmarked selected physical declaration");
        }
    }
}
