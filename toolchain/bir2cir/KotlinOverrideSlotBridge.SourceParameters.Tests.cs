using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static void SourceParameterSnapshotSelfTest()
    {
        var own = new TypeNode.Fqn("Own");
        var outer = new TypeNode.Fqn("Outer");
        var sourceType = new TypeNode.Fqn("Container.Inner", new TypeNode[] { own, outer });
        var physicalType = new TypeNode.Fqn(sourceType.Name, new TypeNode[] { outer, own });
        BirFunctionDeclaration Method(string id) => new() { Name = "same", DeclarationId = id,
            Params = new[] { new BirParameter { Name = "value", Type = sourceType } } };
        var discarded = Method("earlier-owner");
        var retained = Method("selected-owner");
        var first = new BirFileDeclaration { FileClass = "First", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Shared", Methods = new[] { discarded } },
        } };
        var second = new BirFileDeclaration { FileClass = "Second", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Shared", Methods = new[] { retained } },
        } };
        var files = new[] { first, second };
        var pending = CaptureSourceParameters(files);
        retained.Params.Value[0].Type = physicalType;
        var source = pending.ReadSourceParameters();
        if (source.Count != 1 || !source.TryGetValue(retained, out var frozen)
            || source.ContainsKey(discarded) || frozen is not [var original] || original != sourceType)
            throw new InvalidOperationException("Typed source override parameters lost their original shapes or selected occurrences");
        var projected = files.Select(file => (JsonNode)file.ToDom()).ToArray();
        var associated = pending.BindDom(projected);
        var actual = (JsonObject)projected[1]["types"][0]["methods"][0];
        if (associated.Count != 1 || !associated.ContainsKey(actual)
            || associated[actual] is not [TypeNode.Fqn { Args: [var originalOwn, var originalOuter] }]
            || originalOwn != own || originalOuter != outer
            || TypeJson.Read(actual["params"][0]["type"]) != physicalType)
            throw new InvalidOperationException("Source override parameters lost their capture clock or actual method association");
        var repeated = false;
        try { pending.BindDom(projected); } catch (InvalidOperationException) { repeated = true; }
        if (!repeated) throw new InvalidOperationException("Source override parameters were bound twice");
        var released = false;
        try { pending.ReadSourceParameters(); } catch (InvalidOperationException) { released = true; }
        if (!released) throw new InvalidOperationException("Source override parameters remained readable after the frontier");

        var replaced = CaptureSourceParameters(files);
        second.Types.Value[0].Methods.Value[0] = (BirFunctionDeclaration)retained.DeepClone();
        var detached = false;
        try { replaced.BindDom(files.Select(file => (JsonNode)file.ToDom()).ToArray()); }
        catch (InvalidOperationException) { detached = true; }
        if (!detached) throw new InvalidOperationException("Source override parameters rebound a copied declaration by name or identity");
        MaterializedSourceParameterSnapshotSelfTest();
        RetiredSourceParameterSnapshotSelfTest();
        Console.WriteLine("[source override parameter snapshots] self-test OK (last owner, pre-projection types, exact continuation, producer appends, one-time handoff)");
    }

    static void RetiredSourceParameterSnapshotSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Override snapshot retirement: " + message); }
        static void Refused(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid declaration retirement was accepted");
        }
        var original = new TypeNode.Fqn("Original");
        var physical = new TypeNode.Fqn("Physical");
        BirFunctionDeclaration Method(string name) => new() { Name = name, DeclarationId = name,
            Params = new[] { new BirParameter { Name = "value", Type = original } }, Ret = original };
        var first = Method("first");
        var removed = Method("placeholder");
        removed.Abstract = true;
        var last = Method("last");
        var property = new BirPropertyDeclaration { Name = "value", Type = original };
        var owner = new BirClassDefinition { Name = "RetirementOwner", Kind = "class",
            Methods = new[] { first, removed, last }, Properties = new[] { property } };
        var file = new BirFileDeclaration { FileClass = "RetirementFile", Types = new IBirTypeDeclaration[] { owner } };
        var roots = new[] { file };
        var pending = CaptureSourceParameters(roots);
        var bindings = new CurrentDeclarationBindings(roots);
        var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
        var removedBinding = bindings.GetCapturedSource(removed);
        var frozen = pending.ReadSourceParameters();
        removed.Params.Value[0].Type = physical;
        var bridge = Method("bridge");
        owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
        bindings.GetSource(bridge);
        Require(pending.AdmitMaterializedMethods(owner, new[] { bridge }, bindings), "physical append was not reported");
        Require(!bindings.TryGetCapturedSource(property, out _), "property acquired an unnecessary detached buyer");
        Refused(() => bindings.RetireSourceMembers(owner, new[] { Method("placeholder") }, Array.Empty<BirPropertyDeclaration>()));
        bindings.RetireSourceMembers(owner, new[] { removed }, new[] { property });
        Require(pending.RetireMethods(owner, new[] { removed }, bindings), "selected owner lost its original row");
        Refused(() => bindings.RetireSourceMembers(owner, new[] { removed }, Array.Empty<BirPropertyDeclaration>()));
        owner.Methods = new[] { first, last, bridge };
        owner.Properties = Array.Empty<BirPropertyDeclaration>();
        Require(pending.ReadSourceParameters().Count == 3 && frozen[removed].Single() == original
            && !frozen.ContainsKey(bridge), "retirement changed original eligibility or frozen parameter values");
        var live = file.ToDom(bindings.ObserveProjection);
        var detached = removed.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        var parameters = pending.BindDom(new JsonNode[] { live });
        var physicalSignatures = signatures.BindPhysical();
        Require(parameters.Count == 3 && parameters[detached].Single() == original
            && physicalSignatures[detached].Single() == original
            && ReferenceEquals(removedBinding.Physical, detached)
            && ReferenceEquals(bindings.GetPhysical(detached), removedBinding)
            && TypeJson.Read(detached["params"][0]["type"]) == physical
            && parameters.ContainsKey(live["types"][0]["methods"][1].AsObject())
            && !parameters.ContainsKey(live["types"][0]["methods"][2].AsObject()),
            "survivor order, exact detached binding or shared frozen-signature continuation changed");

        var stillOwned = Method("stillOwned");
        var stillOwner = new BirClassDefinition { Name = "StillOwner", Methods = new[] { stillOwned } };
        var stillFile = new BirFileDeclaration { FileClass = "StillFile", Types = new IBirTypeDeclaration[] { stillOwner } };
        var stillBindings = new CurrentDeclarationBindings(new[] { stillFile });
        stillBindings.RetireSourceMembers(stillOwner, new[] { stillOwned }, Array.Empty<BirPropertyDeclaration>());
        stillFile.ToDom(stillBindings.ObserveProjection);
        Refused(stillBindings.BindProjected);
    }

    static void MaterializedSourceParameterSnapshotSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Override snapshot materialization: " + message); }
        static void Refused(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Override snapshot accepted an unreported or changed method occurrence");
        }
        var own = new TypeNode.Fqn("Own");
        var outer = new TypeNode.Fqn("Outer");
        var sourceType = new TypeNode.Fqn("Container.Inner", new TypeNode[] { own, outer });
        var physicalType = new TypeNode.Fqn(sourceType.Name, new TypeNode[] { outer, own });
        (BirFileDeclaration File, BirClassDefinition Owner, PendingSourceParameters Pending,
            CurrentDeclarationBindings Bindings, IReadOnlyList<BirFunctionDeclaration> Appended) Fixture(bool report = true, bool produce = true)
        {
            BirFunctionDeclaration Method(string name) => new() { Name = name, DeclarationId = name,
                Params = new[] { new BirParameter { Name = "value", Type = sourceType } }, Ret = sourceType,
                Body = Array.Empty<IBirStatement>() };
            var owner = new BirClassDefinition { Name = "Storage", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { Method("first"), Method("second") },
                Fields = new[] { new BirFieldDeclaration { Name = "value", Type = new TypeNode.Tv("type", 0) } } };
            var file = new BirFileDeclaration { FileClass = "StorageFile", Types = new IBirTypeDeclaration[] { owner } };
            var pending = CaptureSourceParameters(new[] { file });
            owner.Methods.Value[0].Params.Value[0].Type = physicalType;
            var bindings = new CurrentDeclarationBindings(new[] { file });
            var appended = produce ? FBoundStarProjectionErasure.AddFieldAccessorsSource("Storage", 1, owner, bindings)
                : Array.Empty<BirFunctionDeclaration>();
            if (produce) Require(appended.Count == 2, "actual field producer did not author both accessors");
            if (report && produce) Require(pending.AdmitMaterializedMethods(owner, appended, bindings), "selected owner lost its original snapshot row");
            return (file, owner, pending, bindings, appended);
        }
        static JsonObject Project(BirFileDeclaration file, CurrentDeclarationBindings bindings)
        {
            var result = file.ToDom(bindings.ObserveProjection);
            bindings.BindProjected();
            return result;
        }
        var valid = Fixture();
        var source = valid.Pending.ReadSourceParameters();
        Require(source.Count == 2 && source.ContainsKey(valid.Owner.Methods.Value[0])
            && !source.ContainsKey(valid.Appended[0]) && !source.ContainsKey(valid.Appended[1])
            && source[valid.Owner.Methods.Value[0]] is [var original] && original == sourceType,
            "typed read recaptured physical parameters or made appended accessors source-eligible");
        Refused(() => valid.Pending.AdmitMaterializedMethods(valid.Owner, valid.Appended, valid.Bindings));
        var projected = Project(valid.File, valid.Bindings);
        var methods = projected["types"][0]["methods"].AsArray();
        var snapshots = valid.Pending.BindDom(new JsonNode[] { projected });
        Require(snapshots.Count == 2 && snapshots[methods[0].AsObject()] is [TypeNode.Fqn { Args: [var first, var second] }]
            && first == own && second == outer && TypeJson.Read(methods[0]["params"][0]["type"]) == physicalType
            && !snapshots.ContainsKey(methods[2].AsObject()) && !snapshots.ContainsKey(methods[3].AsObject()),
            "appended accessors acquired source eligibility or changed original parameter capture");

        var unreported = Fixture(false);
        var unreportedProjection = Project(unreported.File, unreported.Bindings);
        Refused(() => unreported.Pending.BindDom(new JsonNode[] { unreportedProjection }));
        var replaced = Fixture();
        var replacedProjection = Project(replaced.File, replaced.Bindings);
        replaced.Owner.Methods.Value[0] = (BirFunctionDeclaration)replaced.Owner.Methods.Value[0].DeepClone();
        Refused(() => replaced.Pending.BindDom(new JsonNode[] { replacedProjection }));
        var reordered = Fixture();
        var reorderedProjection = Project(reordered.File, reordered.Bindings);
        (reordered.Owner.Methods.Value[0], reordered.Owner.Methods.Value[1]) =
            (reordered.Owner.Methods.Value[1], reordered.Owner.Methods.Value[0]);
        Refused(() => reordered.Pending.BindDom(new JsonNode[] { reorderedProjection }));
        var changedTail = Fixture();
        var changedTailProjection = Project(changedTail.File, changedTail.Bindings);
        changedTail.Owner.Methods.Value[2] = (BirFunctionDeclaration)changedTail.Appended[0].DeepClone();
        Refused(() => changedTail.Pending.BindDom(new JsonNode[] { changedTailProjection }));
        var reorderedTail = Fixture();
        var reorderedTailProjection = Project(reorderedTail.File, reorderedTail.Bindings);
        (reorderedTail.Owner.Methods.Value[2], reorderedTail.Owner.Methods.Value[3]) =
            (reorderedTail.Owner.Methods.Value[3], reorderedTail.Owner.Methods.Value[2]);
        Refused(() => reorderedTail.Pending.BindDom(new JsonNode[] { reorderedTailProjection }));
        var projectedCopy = Fixture();
        var projectedCopyRoot = Project(projectedCopy.File, projectedCopy.Bindings);
        var projectedMethods = projectedCopyRoot["types"][0]["methods"].AsArray();
        projectedMethods[2] = projectedMethods[2].DeepClone();
        Refused(() => projectedCopy.Pending.BindDom(new JsonNode[] { projectedCopyRoot }));
        var unauthorized = Fixture();
        var unauthorizedProjection = Project(unauthorized.File, unauthorized.Bindings);
        unauthorized.Owner.Methods = unauthorized.Owner.Methods.Value.Append(new BirFunctionDeclaration { Name = "unreported" }).ToArray();
        Refused(() => unauthorized.Pending.BindDom(new JsonNode[] { unauthorizedProjection }));

        // The discarded same-name occurrence is not made source-eligible by a
        // physical append report. Its producer still owns the exact continuation.
        var earlier = Fixture(false, false);
        var selected = Fixture(false, false);
        earlier.Owner.Methods.Value[0].DeclarationId = "earlier-first";
        earlier.Owner.Methods.Value[1].DeclarationId = "earlier-second";
        var duplicateFiles = new[] { earlier.File, selected.File };
        var lastWins = CaptureSourceParameters(duplicateFiles);
        var duplicateBindings = new CurrentDeclarationBindings(duplicateFiles);
        earlier.Owner.Fields = earlier.Owner.Fields.Value.Append(new BirFieldDeclaration {
            Name = "later", Type = new TypeNode.Tv("type", 0) }).ToArray();
        var earlierAppend = FBoundStarProjectionErasure.AddFieldAccessorsSource("Storage", 1, earlier.Owner, duplicateBindings);
        Require(!lastWins.AdmitMaterializedMethods(earlier.Owner, earlierAppend, duplicateBindings),
            "nonselected actual owner matched a selected row by name");
        var duplicateProjection = duplicateFiles.Select(file => (JsonNode)file.ToDom(duplicateBindings.ObserveProjection)).ToArray();
        duplicateBindings.BindProjected();
        var lastSnapshots = lastWins.BindDom(duplicateProjection);
        Require(lastSnapshots.Count == selected.Owner.Methods.Value.Length
            && !lastSnapshots.ContainsKey(duplicateProjection[0]["types"][0]["methods"][0].AsObject()),
            "last-owner snapshot membership changed after a noneligible occurrence append");
    }
}
