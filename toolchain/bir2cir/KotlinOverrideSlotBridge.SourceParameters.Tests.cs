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
        pending.Complete();
        if (source[retained] is not [TypeNode.Fqn { Args: [var originalOwn, var originalOuter] }]
            || originalOwn != own || originalOuter != outer || retained.Params.Value[0].Type.Value != physicalType)
            throw new InvalidOperationException("Source override parameters lost their capture clock or actual method association");
        var repeated = false;
        try { pending.Complete(); } catch (InvalidOperationException) { repeated = true; }
        if (!repeated) throw new InvalidOperationException("Source override parameters were completed twice");
        var released = false;
        try { pending.ReadSourceParameters(); } catch (InvalidOperationException) { released = true; }
        if (!released) throw new InvalidOperationException("Source override parameters remained readable after the frontier");

        var replaced = CaptureSourceParameters(files);
        second.Types.Value[0].Methods.Value[0] = (BirFunctionDeclaration)retained.DeepClone();
        var detached = false;
        try { replaced.Complete(); }
        catch (InvalidOperationException) { detached = true; }
        if (!detached) throw new InvalidOperationException("Source override parameters rebound a copied declaration by name or identity");
        MaterializedSourceParameterSnapshotSelfTest();
        CurrentDeclarationRetirementSelfTest();
        Console.WriteLine("[source override parameter snapshots] self-test OK (last owner, frozen types, exact occurrences, producer appends, completion)");
    }

    static void CurrentDeclarationRetirementSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Current declaration retirement: " + message); }
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
        var bindings = new CurrentDeclarationBindings(roots);
        var signatures = FBoundStarProjectionErasure.CaptureSourceSignatures(bindings);
        var removedBinding = bindings.GetCapturedSource(removed);
        removed.Params.Value[0].Type = physical;
        var bridge = Method("bridge");
        owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
        bindings.GetSource(bridge);
        Require(!bindings.TryGetCapturedSource(property, out _), "property acquired an unnecessary detached buyer");
        Refused(() => bindings.RetireSourceMembers(owner, new[] { Method("placeholder") }, Array.Empty<BirPropertyDeclaration>()));
        bindings.RetireSourceMembers(owner, new[] { removed }, new[] { property });
        Refused(() => bindings.RetireSourceMembers(owner, new[] { removed }, Array.Empty<BirPropertyDeclaration>()));
        owner.Methods = new[] { first, last, bridge };
        owner.Properties = Array.Empty<BirPropertyDeclaration>();
        var live = file.ToDom(bindings.ObserveProjection);
        var detached = removed.ToDom(bindings.ObserveProjection);
        bindings.BindProjected();
        var physicalSignatures = signatures.BindPhysical();
        Require(physicalSignatures.Count == 3 && physicalSignatures[detached].Single() == original
            && ReferenceEquals(removedBinding.Physical, detached)
            && ReferenceEquals(bindings.GetPhysical(detached), removedBinding)
            && TypeJson.Read(detached["params"][0]["type"]) == physical
            && physicalSignatures.ContainsKey(live["types"][0]["methods"][1].AsObject())
            && !physicalSignatures.ContainsKey(live["types"][0]["methods"][2].AsObject()),
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
        var valid = Fixture();
        var source = valid.Pending.ReadSourceParameters();
        Require(source.Count == 2 && source.ContainsKey(valid.Owner.Methods.Value[0])
            && !source.ContainsKey(valid.Appended[0]) && !source.ContainsKey(valid.Appended[1])
            && source[valid.Owner.Methods.Value[0]] is [var original] && original == sourceType,
            "typed read recaptured physical parameters or made appended accessors source-eligible");
        Refused(() => valid.Pending.AdmitMaterializedMethods(valid.Owner, valid.Appended, valid.Bindings));
        valid.Pending.Complete();
        Require(source.Count == 2 && source[valid.Owner.Methods.Value[0]] is [TypeNode.Fqn { Args: [var first, var second] }]
            && first == own && second == outer && valid.Owner.Methods.Value[0].Params.Value[0].Type.Value == physicalType
            && !source.ContainsKey(valid.Appended[0]) && !source.ContainsKey(valid.Appended[1]),
            "appended accessors acquired source eligibility or changed original parameter capture");

        var unreported = Fixture(false);
        Refused(unreported.Pending.Complete);
        var replaced = Fixture();
        replaced.Owner.Methods.Value[0] = (BirFunctionDeclaration)replaced.Owner.Methods.Value[0].DeepClone();
        Refused(replaced.Pending.Complete);
        var reordered = Fixture();
        (reordered.Owner.Methods.Value[0], reordered.Owner.Methods.Value[1]) =
            (reordered.Owner.Methods.Value[1], reordered.Owner.Methods.Value[0]);
        Refused(reordered.Pending.Complete);
        var changedTail = Fixture();
        changedTail.Owner.Methods.Value[2] = (BirFunctionDeclaration)changedTail.Appended[0].DeepClone();
        Refused(changedTail.Pending.Complete);
        var reorderedTail = Fixture();
        (reorderedTail.Owner.Methods.Value[2], reorderedTail.Owner.Methods.Value[3]) =
            (reorderedTail.Owner.Methods.Value[3], reorderedTail.Owner.Methods.Value[2]);
        Refused(reorderedTail.Pending.Complete);
        var unauthorized = Fixture();
        unauthorized.Owner.Methods = unauthorized.Owner.Methods.Value.Append(new BirFunctionDeclaration { Name = "unreported" }).ToArray();
        Refused(unauthorized.Pending.Complete);

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
        var lastSnapshots = lastWins.ReadSourceParameters();
        lastWins.Complete();
        Require(lastSnapshots.Count == selected.Owner.Methods.Value.Length
            && !lastSnapshots.ContainsKey(earlier.Owner.Methods.Value[0]),
            "last-owner snapshot membership changed after a noneligible occurrence append");
    }
}
