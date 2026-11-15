using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    static void NativeRefreshSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Physical native refresh: " + message); }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Physical native refresh did not reject " + message);
        }
        var text = new TypeNode.Fqn("kotlin.String");
        var ordinary = new BirFieldDeclaration { Name = "ordinary", Type = text };
        var other = new BirFieldDeclaration { Name = "other", Type = text };
        var file = new BirFileDeclaration { FileClass = "Refresh", Fields = new[] { ordinary, other } };
        var policy = CreateSource(new[] { file }, new Dictionary<string, string>(), null);
        policy.PrepareDomLocations(new[] { file });
        var originalExpected = policy._pendingNativePhysicalBindings;
        var originalOrigins = policy._pendingNativeOrigins;
        BirFieldGet Field(string owner, string name) => new() { OwnerType = new TypeNode.Fqn(owner), Name = name,
            Recv = new BirThis() };
        var use = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Refresh"), Name = "ordinary" };
        ordinary.Static = true;
        var exported = new BirFieldDeclaration { Name = "exported", Type = text,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        file.Fields = new[] { ordinary, other, exported };
        var capture = new BirFieldDeclaration { Name = "captured", Type = text };
        var owner = new BirClassDefinition { Name = "Capture", Kind = "class", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = new[] { capture } };
        file.Types = new IBirTypeDeclaration[] { owner };
        var cell = new BirRefTypeDeclaration { Name = "LateCell", Elem = text };
        file.RefTypes = new[] { cell };
        var captureUse = Field("Capture", "captured");
        var cellUse = Field("LateCell", "v");
        var untouchedUse = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Refresh"), Name = "other" };
        // The complete-module call vector is selected now. The early source
        // selection did not see any of these address uses or the new storage.
        file.Methods = new[] { new BirFunctionDeclaration { Name = "pass", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = new BirCallStatic { Method = "consume",
                    ShapeTypes = new TypeNode[] { new TypeNode.ByRef(text) }, Sig = new TypeNode[] { text },
                    Args = new IBirExpression[] { new BirByRefOf { Inner = use } } } },
                new BirExpressionStatement { Expr = new BirNativeStatic { Method = "consume", Sig = new TypeNode[] {
                    new TypeNode.ByRef(text), new TypeNode.ByRef(text) }, Args = new IBirExpression[] { captureUse, cellUse } } },
                new BirExpressionStatement { Expr = new BirCallStatic { Method = "ordinary", ShapeTypes = new TypeNode[] { text },
                    Sig = new TypeNode[] { new TypeNode.ByRef(text) }, Args = new IBirExpression[] { untouchedUse } } },
            } } };
        var control = file.ToDom();
        policy.RefreshPhysicalNativeLocations(new[] { file });
        var continuation = file.ToDom();
        // This old late consumer remains real during the connected migration;
        // compare the same current physical input, not an old-format fixture.
        var domPolicy = new GenericRepresentationPolicy(new Dictionary<string, string>());
        domPolicy.RecordNativeFields(new[] { control });
        Require(JsonNode.DeepEquals(control, continuation), "typed and current DOM physical selection differ");
        var empty = Array.Empty<TypeNode>();
        var fallback = new TypeNode[] { new TypeNode.ByRef(text) };
        Require(ReferenceEquals(NativeRefreshParameters(empty, fallback, fallback, fallback), empty)
            && ReferenceEquals(NativeRefreshParameters<TypeNode[]>(null, null, fallback, null), fallback),
            "selected empty/null vector precedence changed");
        Require(policy._sourceNativeStorage.Count == 0 && ReferenceEquals(originalExpected, policy._pendingNativePhysicalBindings)
            && ReferenceEquals(originalOrigins, policy._pendingNativeOrigins) && originalExpected.Count == 0
            && originalOrigins.Count == 0 && ordinary._nativeLocation.OrDefault() && exported._nativeLocation.OrDefault()
            && capture._nativeLocation.OrDefault() && cell._nativeLocation.OrDefault()
            && use._nativeFieldAddress.OrDefault() && captureUse._nativeFieldAddress.OrDefault()
            && cellUse._nativeFieldAddress.OrDefault() && !other._nativeLocation.IsPresent && !untouchedUse._nativeLocation.IsPresent
            && ReferenceEquals(file.Fields.Value[0], ordinary) && ReferenceEquals(owner.Fields.Value[0], capture)
            && policy.IsNativeField(ordinary) && policy.IsNativeField(capture) && policy.IsNativeField(cell)
            && policy.ProjectArgumentHead(new TypeNode.Fqn("Capture", new TypeNode[] { text }), true, null)
                == new TypeNode.Fqn("Capture", new TypeNode[] { text }),
            "late physical admission changed original source eligibility/origins or selected the wrong vector/staticness");
        Reject(() => policy.RefreshPhysicalNativeLocations(new[] { file }), "duplicate refresh");
        // Carrier/foreign producers append after refresh. They report exact
        // records, without rerunning physical/native selection or source capture.
        var originalTypes = file.Types.Value;
        var carrier = new BirInterfaceDefinition { Name = "Carrier", Kind = "interface", Generated = true };
        file.Types = file.Types.Value.Append(carrier).ToArray();
        policy.AdmitMaterializedNativeTypes(file, new IBirTypeDeclaration[] { carrier });
        var notSelected = new BirFieldDeclaration { Name = "notSelectedByRefresh", Type = text,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var materialized = new BirClassDefinition { Name = "Materialized", Kind = "class", Generated = true,
            Fields = new[] { notSelected } };
        file.Types = file.Types.Value.Append(materialized).ToArray();
        policy.AdmitMaterializedNativeTypes(file, new IBirTypeDeclaration[] { materialized });
        Require(policy._nativeRefreshTypeVectors[0].Original.SequenceEqual(originalTypes, ReferenceEqualityComparer.Instance)
            && policy._nativeRefreshTypeVectors[0].Continuing.SequenceEqual(file.Types.Value, ReferenceEqualityComparer.Instance)
            && ReferenceEquals(originalExpected, policy._pendingNativePhysicalBindings)
            && ReferenceEquals(originalOrigins, policy._pendingNativeOrigins) && !notSelected._nativeLocation.IsPresent,
            "reported type append changed the original vector/origin authority or selected new native storage");
        // The following normalization clock can change structured type facts,
        // but not the field's physical identity or actual owning occurrence.
        exported.Type = new TypeNode.Fqn("Normalized", new TypeNode[] { text });
        continuation = file.ToDom();
        policy.BindDomDeclarations(new[] { continuation });
        Require(policy._refreshedNativeLocations == null && policy._nativeRefreshRoots == null && policy._nativeRefreshTypeVectors == null
            && policy._pendingNativeOrigins == null && policy._pendingNativePhysicalBindings == null
            && policy._nativeFields.Count == 4
            && TypeNodeDataEquality.Equal(exported.Type.Value, TypeJson.Read(continuation["fields"][2]["type"])),
            "physical/native source handoff did not preserve live normalization or release exactly once");
        Reject(() => policy.AdmitMaterializedNativeTypes(file, Array.Empty<IBirTypeDeclaration>()), "type admission after handoff");
        var bound = continuation["fields"][0].AsObject();
        bound.Remove(NativeLocationKey);
        bound["name"] = "renamedAfterBinding";
        Require(policy.IsNativeField(bound), "late admission retained only a name/marker, not the actual continuing record");

        // Original native origins remain strict even if the refresh discovers
        // an otherwise valid current physical declaration with the same role.
        foreach (var replacementName in new[] { "original", "newPhysical" })
        {
            var origin = new BirFieldDeclaration { Name = "original", Type = text,
                Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
            var missing = new BirFileDeclaration { FileClass = "Missing", Fields = new[] { origin } };
            var missingPolicy = CreateSource(new[] { missing }, new Dictionary<string, string>(), null);
            missingPolicy.PrepareDomLocations(new[] { missing });
            var replacement = new BirFieldDeclaration { Name = replacementName, Type = text,
                Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
            missing.Fields = new[] { replacement };
            Reject(() => {
                missingPolicy.RefreshPhysicalNativeLocations(new[] { missing });
                missingPolicy.BindDomDeclarations(new[] { missing.ToDom() });
            }, "missing original source native origin (" + replacementName + ")");
            Require(missingPolicy._sourceNativeStorage.Contains(origin)
                && missingPolicy._pendingNativePhysicalBindings.Count == 1
                && missingPolicy._pendingNativeOrigins.ContainsKey(origin)
                && missingPolicy._nativeStorageMaterializations.Count == 0
                && (replacementName != "original" || !replacement._nativeLocation.IsPresent),
                "failed original-origin admission consumed/widened its ledger or stamped a same-key replacement");
        }

        // Admission carries an actual owning occurrence, not a recoverable
        // name. Replacing it by an equal snapshot must not silently rebind it.
        var replacementFile = new BirFileDeclaration { FileClass = "Replacement" };
        var replacementPolicy = CreateSource(new[] { replacementFile }, new Dictionary<string, string>(), null);
        replacementPolicy.PrepareDomLocations(new[] { replacementFile });
        var selected = new BirFieldDeclaration { Name = "selected", Type = text,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        replacementFile.Fields = new[] { selected };
        replacementPolicy.RefreshPhysicalNativeLocations(new[] { replacementFile });
        replacementFile.Fields = new[] { (BirFieldDeclaration)selected.DeepClone() };
        Reject(() => replacementPolicy.BindDomDeclarations(new[] { replacementFile.ToDom() }), "replaced physical admission");

        var duplicateFile = new BirFileDeclaration { FileClass = "Duplicate" };
        var duplicatePolicy = CreateSource(new[] { duplicateFile }, new Dictionary<string, string>(), null);
        duplicatePolicy.PrepareDomLocations(new[] { duplicateFile });
        duplicateFile.Fields = new[] { selected, selected };
        Reject(() => duplicatePolicy.RefreshPhysicalNativeLocations(new[] { duplicateFile }), "duplicate owning native admission");

        (GenericRepresentationPolicy Policy, BirFileDeclaration File, BirClassDefinition Owner) Pending()
        {
            var field = new BirFieldDeclaration { Name = "original", Type = text,
                Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
            var originalOwner = new BirClassDefinition { Name = "Original", Kind = "class", Fields = new[] { field } };
            var pendingFile = new BirFileDeclaration { FileClass = "Append",
                Types = new IBirTypeDeclaration[] { originalOwner, new BirInterfaceDefinition { Name = "Second", Kind = "interface" } } };
            var pendingPolicy = CreateSource(new[] { pendingFile }, new Dictionary<string, string>(), null);
            pendingPolicy.PrepareDomLocations(new[] { pendingFile });
            pendingPolicy.RefreshPhysicalNativeLocations(new[] { pendingFile });
            return (pendingPolicy, pendingFile, originalOwner);
        }
        foreach (var change in new[] { "reorder", "replace", "delete", "wrongReportedRecord", "duplicate", "unknownFile" })
        {
            var pending = Pending();
            var before = pending.File.Types.Value;
            var appended = new BirClassDefinition { Name = "Appended", Kind = "class", Generated = true };
            pending.File.Types = before.Append(appended).ToArray();
            if (change == "reorder") pending.File.Types = new IBirTypeDeclaration[] { before[1], before[0], appended };
            if (change == "replace") pending.File.Types = new IBirTypeDeclaration[] { (BirClassDefinition)pending.Owner.DeepClone(), before[1], appended };
            if (change == "delete") pending.File.Types = new IBirTypeDeclaration[] { before[0], appended };
            if (change == "duplicate") pending.File.Types = before.Concat(new[] { appended, appended }).ToArray();
            Reject(() => pending.Policy.AdmitMaterializedNativeTypes(
                change == "unknownFile" ? (BirFileDeclaration)pending.File.DeepClone() : pending.File,
                change == "duplicate" ? new IBirTypeDeclaration[] { appended, appended }
                    : new IBirTypeDeclaration[] { change == "wrongReportedRecord" ? (BirClassDefinition)appended.DeepClone() : appended }),
                "unreported type disposition (" + change + ")");
            Require(pending.Policy._nativeRefreshTypeVectors[0].Continuing.SequenceEqual(before, ReferenceEqualityComparer.Instance),
                "failed append admission changed the continuation vector");
        }
        foreach (var report in new[] { false, true })
        {
            var pending = Pending();
            var appended = new BirClassDefinition { Name = "Appended", Kind = "class", Generated = true };
            pending.File.Types = pending.File.Types.Value.Append(appended).ToArray();
            if (report)
            {
                pending.Policy.AdmitMaterializedNativeTypes(pending.File, new IBirTypeDeclaration[] { appended });
                pending.Owner.Fields = new[] { (BirFieldDeclaration)pending.Owner.Fields.Value[0].DeepClone() };
            }
            Reject(() => pending.Policy.BindDomDeclarations(new[] { pending.File.ToDom() }),
                report ? "original field replacement after a reported append" : "unreported appended type");
        }
        Console.WriteLine("[physical native refresh] self-test OK (late storage, exact appended types, strict original prefixes/origins, one handoff and release)");
    }
}
