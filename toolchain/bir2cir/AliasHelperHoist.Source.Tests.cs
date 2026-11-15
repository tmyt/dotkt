using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class AliasHelperHoist
{
    internal static void SelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Alias representation: " + message); }
        var frame = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 3, 0, 4, 1, 2, 5 },
            storageIndices: new[] { 0, 1 }, nullableStorageIndices: new[] { 0 });
        var policy = new GenericRepresentationPolicy(new Dictionary<string, string> { ["Alias"] = "Native" });
        var receiver = (TypeNode.Fqn)ReceiverType("Alias", frame, policy);
        Require(receiver.Args.Cast<TypeNode.Tv>().Select(type => type.I).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }),
            "receiver lost binding roles or physical permutation");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var callback = new TypeNode.Fn(false, methodVariable, new TypeNode[] { new TypeNode.Tv("type", 1) });
        var construction = new BirNew { Type = new TypeNode.Fqn("Target", new TypeNode[] { ownerVariable }),
            ArgTypes = new TypeNode[] { ownerVariable }, MemberSignature = new TypeNode[] { ownerVariable },
            Args = new IBirExpression[] { new BirThis() } };
        var suspend = new BirNewSuspendLambda { Arity = 0, CapValues = new IBirExpression[] { new BirThis() },
            Body = new IBirStatement[] { new BirReturn { Value = new BirThis() } } };
        var source = new BirFunctionDeclaration { Name = "Read", DeclarationId = "alias-test",
            DeclarationSourceName = "Read", TypeParams = new[] { new BirTypeParameter { Name = "R" } },
            Params = new[] { new BirParameter { Name = "callback", Type = callback, FunctionSignatureType = callback } },
            Ret = new TypeNode.Tv("type", 1), Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = construction }, new BirExpressionStatement { Expr = suspend } } };
        var parameters = new[] { "S0", "T0", "S1", "T1", "N0", "NS0" }
            .Select(name => new BirTypeParameter { Name = name }).ToArray();
        var helper = HoistMethod(source, parameters, receiver);
        Require(helper.DeclarationId.Value == DeclarationIdentity("alias-test") && helper.DeclarationSourceName.Value == "Read"
            && helper.Generated.Value && helper.Static.Value && helper.TypeParams.Value.Length == 7
            && helper.Ret.Value == new TypeNode.Tv("method", 1), "helper identity, flags or frame changed");
        Require(helper.Params.Value[1].FunctionSignatureType.Value is TypeNode.Fn signature
            && signature.Ret == new TypeNode.Tv("method", 6) && signature.Params[0] == new TypeNode.Tv("method", 1),
            "function signature frame was not moved");
        var hoistedNew = (BirNew)((BirExpressionStatement)helper.Body.Value[0]).Expr.Value;
        var hoistedSuspend = (BirNewSuspendLambda)((BirExpressionStatement)helper.Body.Value[1]).Expr.Value;
        Require(hoistedNew.ArgTypes.Value[0] == new TypeNode.Tv("method", 0)
            && hoistedNew.MemberSignature.Value[0] == ownerVariable
            && hoistedNew.Args.Value[0] is BirLocal { Name.Value: "__self" }
            && hoistedSuspend.CapValues.Value[0] is BirLocal { Name.Value: "__self" }
            && ((BirReturn)hoistedSuspend.Body.Value[0]).Value.Value is BirThis
            && construction.Args.Value[0] is BirThis && source.Ret.Value == new TypeNode.Tv("type", 1),
            "source identity, foreign descriptor or suspend receiver boundary changed");

        var target = new BirFunctionDeclaration { Name = "lifted", SemanticOwner = "kotlin.Array", Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "R" } },
            Params = new[] { new BirParameter { Name = "value", Type = ownerVariable } }, Ret = methodVariable,
            Body = Array.Empty<IBirStatement>() };
        var edge = new BirNewDelegate { Method = "lifted", CalleeOwner = new TypeNode.Fqn("kotlin.Array"),
            TypeArgs = new TypeNode[] { methodVariable, ownerVariable } };
        var plain = new BirClassDefinition { Name = "Kept", Kind = "class", Methods = new[] { source } };
        var alias = new BirClassDefinition { Name = "kotlin.Array", Kind = "class" };
        var rehomeRoot = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { alias, plain },
            Methods = new[] { target, new BirFunctionDeclaration { Body = new IBirStatement[] { new BirExpressionStatement { Expr = edge } } } } };
        var transitions = new AliasDeclarationTransitions();
        Apply(rehomeRoot, ReferenceMetadataIndex.Build(Array.Empty<string>()), policy, transitions, _ => { });
        Require(ReferenceEquals(rehomeRoot.Types.Value[0], plain) && ReferenceEquals(plain.Methods.Value[0], source)
            && ReferenceEquals(rehomeRoot.Methods.Value[0], target) && target.SemanticOwner.Value == "Facade"
            && target.Params.Value[0].Type.Value == new TypeNode.Tv("method", 1)
            && target.Ret.Value == methodVariable && edge.CalleeOwner.Value == new TypeNode.Fqn("Facade"),
            "kept records or authored generated-helper frame association changed");
        transitions.ValidateSourceContinuations(new[] { rehomeRoot });
        var retained = rehomeRoot.Types.Value;
        rehomeRoot.Types = new IBirTypeDeclaration[] { (BirClassDefinition)plain.DeepClone() };
        var refused = false;
        try { transitions.ValidateSourceContinuations(new[] { rehomeRoot }); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "an unreported replacement was accepted");
        rehomeRoot.Types = retained;
        transitions.Release();
        AliasHandoffSelfTest();
        MaterializedAppendSelfTest();
        OwnedVisibilitySelfTest();
        Console.WriteLine("[alias helper receiver] self-test OK (owning identities, exact disposition, binding roles, permutation, scope transfer)");
    }

    static void MaterializedAppendSelfTest()
    {
        var alias = new BirClassDefinition { Name = "Alias", Kind = "class" };
        var kept = new BirClassDefinition { Name = "Kept", Kind = "class" };
        var host = new BirClassDefinition { Name = "Host", Kind = "class" };
        var helper = new BirClassDefinition { Name = "Helper", Kind = "class" };
        var first = new BirClassDefinition { Name = "FirstCallback", Kind = "class" };
        var second = new BirClassDefinition { Name = "SecondCallback", Kind = "class" };
        var original = new IBirTypeDeclaration[] { alias, kept };
        var continuing = new IBirTypeDeclaration[] { host, helper, kept };
        var file = new BirFileDeclaration { FileClass = "Appended", Types = continuing };
        var transitions = new AliasDeclarationTransitions();
        transitions.Record(file, original, continuing, new[] { new AliasDeclarationTransitions.Replacement(alias, host, helper) });
        static void Refused(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Alias materialization accepted an unreported occurrence transition");
        }
        file.Types = new IBirTypeDeclaration[] { host, helper, kept, first };
        Refused(() => transitions.ValidateSourceContinuations(new[] { file }));
        file.Types = new IBirTypeDeclaration[] { host, kept, helper, first };
        Refused(() => transitions.AdmitMaterializedTypes(file, new[] { first }));
        file.Types = new IBirTypeDeclaration[] { host, helper, first };
        Refused(() => transitions.AdmitMaterializedTypes(file, new[] { first }));
        file.Types = new IBirTypeDeclaration[] { host, helper, (BirClassDefinition)kept.DeepClone(), first };
        Refused(() => transitions.AdmitMaterializedTypes(file, new[] { first }));
        Refused(() => transitions.AdmitMaterializedTypes(new BirFileDeclaration(), Array.Empty<BirClassDefinition>()));
        file.Types = new IBirTypeDeclaration[] { host, helper, kept, first };
        transitions.AdmitMaterializedTypes(file, new[] { first });
        file.Types = new IBirTypeDeclaration[] { host, helper, kept, first, second };
        Refused(() => transitions.AdmitMaterializedTypes(file, new[] { first }));
        transitions.AdmitMaterializedTypes(file, new[] { second });
        var contract = new BirInterfaceDefinition { Name = "ReferenceContract", Kind = "interface" };
        file.Types = file.Types.OrEmpty().Concat(new IBirTypeDeclaration[] { contract }).ToArray();
        transitions.AdmitMaterializedTypes(file, new IBirTypeDeclaration[] { contract });
        transitions.ValidateSourceContinuations(new[] { file });
        if (!transitions.IsReplacedLogicalOwner(alias) || transitions.IsReplacedLogicalOwner(kept)
            || !transitions.TryOwner(file, 1, kept, out var position, out var retired) || position != 2 || retired
            || !transitions.TryOwner(file, 0, alias, out _, out retired) || !retired
            || !ReferenceEquals(original[0], alias) || !ReferenceEquals(continuing[2], kept))
            throw new InvalidOperationException("Alias append changed original logical owners or exact continuing positions");
        // No representation ledger is the normal reference-build capability.
        new AliasDeclarationTransitions().AdmitMaterializedTypes(file, new[] { second });
        transitions.Release();
    }

    static void OwnedVisibilitySelfTest()
    {
        foreach (var reverse in new[] { false, true })
        {
            var alias = new BirClassDefinition { Name = "kotlin.Array", Kind = "class" };
            var physical = new BirClassDefinition { Name = "PhysicalChild", Kind = "class", NestedIn = alias.Name, Vis = "private" };
            var semantic = new BirClassDefinition { Name = "SemanticChild", Kind = "class", SemanticOwner = alias.Name, Vis = "private" };
            var unrelated = new BirClassDefinition { Name = "Unrelated", Kind = "class", Vis = "private" };
            IBirTypeDeclaration[] declarations = { alias, physical, semantic, unrelated };
            var file = new BirFileDeclaration { Types = reverse ? declarations.Reverse().ToArray() : declarations };
            var transitions = new AliasDeclarationTransitions();
            Apply(file, ReferenceMetadataIndex.Build(Array.Empty<string>()),
                new GenericRepresentationPolicy(new Dictionary<string, string>()), transitions, _ => { });
            if (physical.Vis.Value != "internal" || semantic.Vis.Value != "internal" || unrelated.Vis.Value != "private"
                || !file.Types.Value.Any(type => ReferenceEquals(type, physical))
                || !file.Types.Value.Any(type => ReferenceEquals(type, semantic))
                || !file.Types.Value.Any(type => ReferenceEquals(type, unrelated)))
                throw new InvalidOperationException("Alias child visibility depends on declaration order or widens an unrelated private declaration");
            transitions.Release();
        }
    }

    static void AliasHandoffSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        BirFunctionDeclaration Method(string id) => new() { Name = "read", DeclarationId = id, Static = false,
            Params = Array.Empty<BirParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "R" } }, Ret = variable,
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = variable,
                E = new BirLocal { Name = "value", Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) } } } } };
        BirFieldDeclaration Field(string name, TypeNode type) => new() { Name = name, Static = true, Type = type,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var moved = Field("companion", new TypeNode.Fqn("Companion"));
        var retired = Field("discarded", new TypeNode.Fqn("kotlin.Int"));
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var firstMethod = Method("retired-alias");
        var keptMethod = Method("kept-member");
        var first = new BirClassDefinition { Name = "kotlin.Array", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T", Variance = "out" } },
            Fields = new[] { moved, retired }, Methods = new[] { firstMethod }, KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame) };
        var removed = new BirClassDefinition { Name = "kotlin.IntArray", Kind = "class" };
        var kept = new BirClassDefinition { Name = "Kept", Kind = "class", Methods = new[] { keptMethod } };
        var child = new BirClassDefinition { Name = "Companion", Kind = "class", SemanticOwner = first.Name,
            CompanionCarrier = new BirCompanionCarrier(), Vis = "private" };
        var original = new IBirTypeDeclaration[] { first, removed, kept, child };
        var file = new BirFileDeclaration { FileClass = "AliasHandoff", Types = original };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var policy = GenericRepresentationPolicy.CreateSource(new[] { file }, new Dictionary<string, string> {
            ["kotlin.Array"] = "System.Array", ["kotlin.IntArray"] = "System.Int32[]" }, refs);
        policy.PrepareSourceLocations(new[] { file });
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(new[] { file });
        UncheckedGenericCastReturnErasure.Apply(file, pending);
        var retiredMethods = new List<BirFunctionDeclaration>();
        var transitions = new AliasDeclarationTransitions();
        Apply(file, refs, policy, transitions, retiredMethods.Add);
        var host = (BirClassDefinition)file.Types.Value[0];
        if (file.Types.Value.Length != 3 || !ReferenceEquals(file.Types.Value[1], kept)
            || !ReferenceEquals(host.Fields.Value[0], moved) || !ReferenceEquals(original[0], first)
            || !ReferenceEquals(first.Methods.Value[0], firstMethod) || child.Vis.Value != "internal")
            throw new InvalidOperationException("Alias whole-type retirement changed kept source records or companion storage");
        var declarations = FBoundStarProjectionErasure.PrepareCurrentDeclarations(new[] { file }, policy, out var preparation);
        var nativeProofs = FBoundStarProjectionErasure.PrepareNativeProofs(declarations, refs, policy);
        var surface = FBoundStarProjectionErasure.PrepareCarrierSurface(preparation, nativeProofs, refs, policy);
        var realization = new FBoundStarProjectionErasure.CarrierRealizationContext(surface,
            FBoundStarProjectionErasure.CarrierProjectionContext.ForSource(surface.Demand, refs), refs,
            surface.Demand.ReservedNames, materializedType: (owner, type) => transitions.AdmitMaterializedTypes(owner, new[] { type }));
        FBoundStarProjectionErasure.RealizeCarriersSource(new[] { file }, realization,
            Array.Empty<SharedSyntheticSynthesis.CellStorage>(), (_, _) => null,
            (owner, types) => transitions.AdmitMaterializedTypes(owner, types));
        var ledger = new SourceDeclarationProjection(new[] { file }, declarations);
        foreach (var method in retiredMethods) ledger.RetireForest(method);
        foreach (var (_, field) in transitions.RetiredFields) ledger.RetireForest(field);
        var retiredFacts = pending.SourceDeclarations.ByIdentity["retired-alias"];
        var keptFacts = pending.SourceDeclarations.ByIdentity["kept-member"];
        ledger.SealSourceContinuations();
        policy.CompleteSourceContinuation(new[] { file }, transitions, ledger);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(pending, new[] { file }, transitions, ledger);
        var logical = AliasDeclarationTransitions.ReadLogicalHeaderFacts(first);
        if (!transitions.TryOwner(file, 0, first, out var aliasPosition, out var aliasRetired)
            || aliasPosition != -1 || !aliasRetired
            || !transitions.TryOwner(file, 2, kept, out var keptPosition, out var keptRetired)
            || keptPosition != 1 || keptRetired || ledger.RetiredTypes.Any()
            || ReferenceEquals(first, host) || logical.KotlinSupertypesPre.Value.NullableFrame.SourceArity != 1
            || logical.TypeParams.Value[0].Variance.Value != "out"
            || retiredFacts.SourceMethod != null || keptFacts.SourceMethod != null
            || retiredFacts.Return != variable || keptFacts.Return != variable
            || !ledger.IsRetired(firstMethod) || ledger.IsRetired(keptMethod)
            || !ReferenceEquals(kept.Methods.Value[0], keptMethod)
            || !transitions.IsReplacedLogicalOwner(first)
            || transitions.IsReplacedLogicalOwner((BirClassDefinition)first.DeepClone()))
            throw new InvalidOperationException("Alias logical facts, retired method or shifted exact owner continuation changed");
        var wrongLogical = (BirClassDefinition)first.DeepClone();
        var wrongRejected = false;
        try { transitions.TryOwner(file, 0, wrongLogical, out _, out _); }
        catch (InvalidOperationException) { wrongRejected = true; }
        if (!wrongRejected) throw new InvalidOperationException("An equal unreported alias owner was admitted");
        FBoundStarProjectionErasure.CompleteRealization(realization);
        if (!ReferenceEquals(file.Types.Value[0], host)
            || AliasDeclarationTransitions.ReadLogicalHeaderFacts(host).KotlinSupertypesPre.OrDefault()?.NullableFrame != null
            || logical.KotlinSupertypesPre.Value.NullableFrame.SourceArity != 1)
            throw new InvalidOperationException("Realization completion conflated a retired logical alias with its actual physical host");
        policy.CheckAliasNativeHandoff(moved, retired, ledger);
        declarations.CompleteSourceCustody(ledger);
        var retainedFields = host.Fields.Value;
        host.Fields = new[] { (BirFieldDeclaration)moved.DeepClone() };
        var refused = false;
        try { transitions.ValidateNativeField(moved); } catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Alias handoff accepted an unreported native field replacement");
        host.Fields = retainedFields;
        transitions.Release();
        ledger.Release();
    }
}

sealed partial class GenericRepresentationPolicy
{
    internal void CheckAliasNativeHandoff(BirFieldDeclaration moved, BirFieldDeclaration retired,
        SourceDeclarationProjection ledger)
    {
        ledger.RequireOccurrence(moved); ledger.RequireOccurrence(retired);
        if (!IsNativeField(moved) || !IsNativeField(retired)
            || ledger.IsRetired(moved) || !ledger.IsRetired(retired)
            || _sourceNativeStorage.Count != 0 || _pendingNativeOrigins != null
            || _pendingNativePhysicalBindings != null)
            throw new InvalidOperationException("Alias native storage lost its exact continuing/detached occurrences or retained source staging");
    }
}
