using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
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
        var cell = new BirRefTypeDeclaration { Name = "SourceCell", Elem = text };
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
        // The old initializer remains live for late physical storage. Compare
        // that implementation on the same current, well-formed authored graph.
        var oldRoot = root.ToDom();
        _ = new GenericRepresentationPolicy(new Dictionary<string, string>(), roots: new[] { oldRoot });
        var policy = CreateSource(new[] { root }, new Dictionary<string, string>(), null);
        var continuation = root.ToDom();
        if (!JsonNode.DeepEquals(oldRoot, continuation)
            || !exported._nativeLocation.OrDefault() || !addressed._nativeLocation.OrDefault()
            || ordinary._nativeLocation.IsPresent || !first._nativeLocation.OrDefault()
            || !second._nativeLocation.OrDefault() || third._nativeLocation.IsPresent
            || !cell._nativeLocation.OrDefault() || !fieldUse._nativeFieldAddress.OrDefault()
            || !companionUse._nativeFieldAddress.OrDefault() || !cellUse._nativeFieldAddress.OrDefault())
            throw new InvalidOperationException("Owning source policy changed native storage selection or companion field identity");
        var sourceApplication = new TypeNode.Fqn("SourceFrame", new TypeNode[] { text });
        var generatedApplication = new TypeNode.Fqn("GeneratedFrame", new TypeNode[] { text });
        if (!policy.OwnsValueSlots("SourceFrame") || !policy.OwnsValueSlots("SourceCell")
            || policy.ProjectArgumentHead(sourceApplication, true, null) is not TypeNode.Fqn { Args: [TypeNode.Star] }
            || policy.ProjectArgumentHead(generatedApplication, true, null) != generatedApplication
            || policy.LocalDeclaration("SourceFrame") != null)
            throw new InvalidOperationException("Owning source policy mixed source eligibility with DOM declaration association");
        policy.BindDomDeclarations(new JsonNode[] { continuation });
        var boundOwner = continuation["types"][0].AsObject();
        var boundField = boundOwner["fields"][1].AsObject();
        if (!ReferenceEquals(policy.LocalDeclaration("SourceFrame"), boundOwner) || !policy.IsNativeField(boundField))
            throw new InvalidOperationException("Source policy did not retain the actual continuing declaration identity");
        boundField.Remove(NativeLocationKey);
        boundField["name"] = "renamedPhysicalStorage";
        if (!policy.IsNativeField(boundField))
            throw new InvalidOperationException("Native storage identity was lost after physical field binding");
        var lateField = new BirFieldDeclaration { Name = "late", Type = text, Attrs = new[] { Export() } }.ToDom();
        boundOwner["fields"].AsArray().Add(lateField);
        policy.RecordNativeFields(new JsonNode[] { continuation });
        var lateOwner = new JsonObject { ["kind"] = "class", ["name"] = "LatePhysical", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }) };
        policy.RegisterLocalDefinitions(new Dictionary<string, JsonObject> { ["LatePhysical"] = lateOwner }, new Dictionary<string, string>());
        var lateApplication = new TypeNode.Fqn("LatePhysical", new TypeNode[] { text });
        if (!policy.IsNativeField(lateField) || lateField[NativeLocationKey]?.GetValue<bool>() != true
            || !policy.OwnsValueSlots("LatePhysical") || policy.ProjectArgumentHead(lateApplication, true, null) != lateApplication)
            throw new InvalidOperationException("Late native storage discovery changed fixed source carrier eligibility");
        Console.WriteLine("[source generic policy] self-test OK (native selection parity, companion/ref-cell storage, one-way identity, fixed eligibility)");
        NativePlacementSelfTest();
    }

    static void NativePlacementSelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var field = new BirFieldDeclaration { Name = "value", Static = true, Type = text,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var source = new BirClassDefinition { Name = "Source", Kind = "class", Fields = new[] { field },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var root = new BirFileDeclaration { FileClass = "Placement", Types = new IBirTypeDeclaration[] { source } };
        var policy = CreateSource(new[] { root }, new Dictionary<string, string>(), null);
        var carrier = new BirClassDefinition { Name = "PhysicalStorage", Kind = "class", Generated = true,
            Fields = new[] { field } };
        field.Name = "physicalValue";
        source.Fields = Array.Empty<BirFieldDeclaration>();
        root.Types = new IBirTypeDeclaration[] { source, carrier };
        policy.PrepareDomLocations(new[] { root });
        var continuation = root.ToDom();
        policy.BindDomDeclarations(new JsonNode[] { continuation });
        if (!policy.IsNativeField(continuation["types"][1]["fields"][0].AsObject())
            || !policy.IsNativeFieldUse("staticField", new TypeNode.Fqn("PhysicalStorage"), "physicalValue", null)
            || policy.IsNativeFieldUse("staticField", new TypeNode.Fqn("Source"), "value", null)
            || policy.ProjectArgumentHead(new TypeNode.Fqn("Source", new[] { text }), true, null)
                is not TypeNode.Fqn { Args: [TypeNode.Star] }
            || policy.ProjectArgumentHead(new TypeNode.Fqn("PhysicalStorage", new[] { text }), true, null)
                is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.String" }] })
            throw new InvalidOperationException("Native physical placement lost selected identity or recomputed source eligibility");
    }
}
