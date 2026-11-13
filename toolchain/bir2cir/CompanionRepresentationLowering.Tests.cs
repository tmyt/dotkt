using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class CompanionRepresentationLoweringTests
{
    const string Semantic = "Probe.Owner.<companion:Named>";
    const string Physical = "Probe.Owner.$Named";

    public static void SelfTest()
    {
        var owner = new BirClassDefinition { Kind = "class", Name = "Probe.Owner" };
        var companion = new BirClassDefinition { Kind = "class", Name = Semantic,
            KotlinCompanion = new BirKotlinCompanion { Owner = "Probe.Owner", Name = "Named", Visibility = "public" } };
        var generic = new BirClassDefinition { Kind = "class", Name = "Probe.Generic",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var genericCompanion = new BirClassDefinition { Kind = "class", Name = "Probe.Generic.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Probe.Generic", Name = "Named", Visibility = "internal" } };
        var child = new BirClassDefinition { Kind = "class", Name = "Probe.Child", SemanticOwner = Semantic, Vis = "private" };
        BirCompanionValue Companion() => new() { CompanionType = new TypeNode.Fqn(Semantic) };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn(Semantic), Recv = Companion(),
            Method = "selected", DeclarationId = "selected-id", Sig = new TypeNode[] { new TypeNode.Fqn("Probe.Parameter") } };
        var bound = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn(Semantic), CalleeOwner = new TypeNode.Fqn("Probe.Sentinel"), Recv = Companion() };
        var inherited = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn("Probe.SelectedOwner"), CalleeOwner = new TypeNode.Fqn("Probe.SelectedOwner"), Recv = Companion() };
        var ordinary = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn(Semantic), CalleeOwner = new TypeNode.Fqn(Semantic), Recv = new BirLocal { Name = "ordinary" } };
        var field = new BirFieldDeclaration { Name = "slot", Type = new TypeNode.Nullable(new TypeNode.Fqn(Semantic)) };
        var root = new BirFileDeclaration { FileClass = "Probe.File",
            Types = new IBirTypeDeclaration[] { owner, companion, generic, genericCompanion, child },
            Fields = new[] { field }, Methods = new[] { new BirFunctionDeclaration { Name = "use",
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = call },
                    new BirExpressionStatement { Expr = bound }, new BirExpressionStatement { Expr = inherited },
                    new BirExpressionStatement { Expr = ordinary } },
            } } };
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        if (!ReferenceEquals(root.Types.Value[1], companion) || companion.Name.Value != Physical
            || companion.NestedIn.Value != "Probe.Owner" || genericCompanion.NestedIn.IsPresent
            || genericCompanion.Name.Value != "Probe.Generic$companion$Named"
            || companion.Fields.Value[0].Name.Value != "$INSTANCE"
            || owner.Fields.Value.Single().Name.Value != "Named"
            || genericCompanion.CompanionCarrier.Value.PhysicalOwnerArity.Value != 1
            || child.SemanticOwner.Value != Physical || child.Vis.Value != "internal"
            || field.Type.Value != new TypeNode.Nullable(new TypeNode.Fqn(Physical))
            || call.OwnerType.Value != new TypeNode.Fqn(Physical) || call.DeclarationId.Value != "selected-id"
            || call.Sig.Value.Single() != new TypeNode.Fqn("Probe.Parameter")
            || call.Recv.Value is not BirStaticFieldGet { Name.Value: "$INSTANCE", Sty.Value: TypeNode.Fqn { Name: Physical } }
            || bound.CalleeOwner.Value != new TypeNode.Fqn(Physical)
            || inherited.OwnerType.Value != new TypeNode.Fqn("Probe.SelectedOwner")
            || ordinary.OwnerType.Value != new TypeNode.Fqn(Physical) || ordinary.CalleeOwner.Value != new TypeNode.Fqn(Physical))
            throw new InvalidOperationException("Typed companion placement lost exact declaration/use/visibility facts");

        // Spliced payloads bind through the same local associations after initial declaration placement.
        SplicedUses(local);
        ExternalUses();
        NestedPlacementOrder();
        Console.WriteLine("[companion representation] self-test OK (uses, selected owners, independent nodes, placement order)");
    }

    static BirCompanionValue Value(string semantic = Semantic) => new() { CompanionType = new TypeNode.Fqn(semantic) };
    static void SplicedUses(CompanionRepresentationLowering.LocalIndex local)
    {
        var field = new BirFieldDeclaration { Name = "slot", Type = new TypeNode.Nullable(new TypeNode.Fqn(Semantic)) };
        var child = new BirClassDefinition { Name = "Probe.Child", SemanticOwner = Semantic };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn(Semantic), Recv = Value(), Method = "selectedMethod",
            DeclarationId = "selected-declaration", Sig = new TypeNode[] { new TypeNode.Fqn("Probe.Parameter") },
            TypeArgs = new TypeNode[] { new TypeNode.Fqn("Probe.Argument") } };
        var get = new BirFieldGet { OwnerType = new TypeNode.Fqn(Semantic), Recv = Value(), Name = "selectedField" };
        var stored = new BirLocal { Name = "stored", Sty = new TypeNode.Fqn("Probe.Unrelated") };
        var set = new BirSetField { OwnerType = new TypeNode.Fqn(Semantic), Recv = Value(), Name = "selectedField", Value = stored };
        var setExpr = new BirFieldSetExpression { OwnerType = new TypeNode.Fqn(Semantic), Recv = Value(), Name = "selectedField", Value = new BirLocal { Name = "rhs" } };
        var late = new BirLateinitGet { OwnerType = new TypeNode.Fqn(Semantic), Recv = Value(), Name = "selectedField" };
        BirNewBoundDelegate Bound(string owner) => new() { OwnerType = new TypeNode.Fqn(owner), Recv = Value(),
            CalleeOwner = new TypeNode.Fqn("Probe.SelectedOwner"), Method = "selectedMethod", DeclarationId = "selected-delegate" };
        var bound = Bound(Semantic);
        var alreadyPhysical = Bound(Physical);
        var inherited = Bound("Probe.SelectedOwner");
        var ordinary = Bound(Semantic);
        ordinary.Recv = new BirLocal { Name = "ordinaryReceiver" };
        ordinary.CalleeOwner = new TypeNode.Fqn(Semantic);
        var unknown = Value("Probe.Unknown.<companion:Named>");
        // An imported physical capture is left untouched in a reference build, except for the transient marker.
        var imported = new BirNew { Type = new TypeNode.Fqn("Imported.Carrier"), CompanionCaptureOwner = "Imported.Carrier" };
        var descriptor = new BirCaptureDescriptor { Name = "companion", Type = new TypeNode.Nullable(new TypeNode.Fqn(Semantic)),
            KotlinType = new TypeNode.Fqn(Semantic) };
        var capturedValue = new BirLocal { Name = "captured", Sty = new TypeNode.Fqn(Semantic) };
        var suspend = new BirNewSuspendLambda { CompanionCaptureOwner = Semantic,
            Captures = new[] { descriptor }, CapValues = new IBirExpression[] { capturedValue, null } };
        var markedClass = new BirClassDefinition { Name = "Probe.Synth", CompanionCaptureOwner = Semantic,
            Fields = new[] { new BirFieldDeclaration { Name = "capture", Type = new TypeNode.Fqn(Semantic) } } };
        var markedNew = new BirNew { Type = new TypeNode.Fqn("Probe.Synth"), CompanionCaptureOwner = Semantic,
            ArgTypes = new TypeNode[] { new TypeNode.Fqn(Semantic) }, Args = new IBirExpression[] { Value() } };
        var expressions = new IBirExpression[] { Value(), Value("Probe.Generic.<companion:Named>"), call, get, setExpr,
            late, bound, alreadyPhysical, inherited, ordinary, unknown, imported, suspend, markedNew };
        var statements = expressions.Select(expr => (IBirStatement)new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expr) })
            .Append(set).ToArray();
        var method = new BirFunctionDeclaration { Name = "spliced", Body = statements };
        var root = new BirFileDeclaration { FileClass = "Spliced", Fields = new[] { field },
            Types = new IBirTypeDeclaration[] { child, markedClass }, Methods = new[] { method } };
        CompanionRepresentationLowering.BindUses(root, local, null, bindExternal: false);
        bool Singleton(IBirExpression expression, string name = Physical) => expression is BirStaticFieldGet load
            && load.Name.OrDefault() == "$INSTANCE" && load.OwnerType.OrDefault() == new TypeNode.Fqn(name)
            && load.Sty.OrDefault() == new TypeNode.Fqn(name);
        if (!ReferenceEquals(root.Methods.Value[0], method) || !ReferenceEquals(method.Body.Value, statements)
            || !ReferenceEquals(root.Types.Value[0], child) || child.SemanticOwner.Value != Physical
            || field.Type.Value != new TypeNode.Nullable(new TypeNode.Fqn(Physical))
            || !Singleton(((BirExpressionStatement)statements[0]).Expr.Value)
            || !Singleton(((BirExpressionStatement)statements[1]).Expr.Value, "Probe.Generic$companion$Named")
            || call.OwnerType.Value != new TypeNode.Fqn(Physical) || !Singleton(call.Recv.Value)
            || call.DeclarationId.Value != "selected-declaration" || call.Sig.Value[0] != new TypeNode.Fqn("Probe.Parameter")
            || call.TypeArgs.Value[0] != new TypeNode.Fqn("Probe.Argument")
            || get.OwnerType.Value != new TypeNode.Fqn(Physical) || !Singleton(get.Recv.Value)
            || set.OwnerType.Value != new TypeNode.Fqn(Physical) || !Singleton(set.Recv.Value) || !ReferenceEquals(set.Value.Value, stored)
            || setExpr.OwnerType.Value != new TypeNode.Fqn(Physical) || !Singleton(setExpr.Recv.Value)
            || late.OwnerType.Value != new TypeNode.Fqn(Physical) || !Singleton(late.Recv.Value)
            || bound.CalleeOwner.Value != new TypeNode.Fqn(Physical) || !Singleton(bound.Recv.Value)
            || alreadyPhysical.CalleeOwner.Value != new TypeNode.Fqn(Physical) || !Singleton(alreadyPhysical.Recv.Value)
            || inherited.OwnerType.Value != new TypeNode.Fqn("Probe.SelectedOwner")
            || inherited.CalleeOwner.Value != new TypeNode.Fqn("Probe.SelectedOwner") || !Singleton(inherited.Recv.Value)
            || ordinary.OwnerType.Value != new TypeNode.Fqn(Physical) || ordinary.CalleeOwner.Value != new TypeNode.Fqn(Physical)
            || !ReferenceEquals(((BirExpressionStatement)statements[10]).Expr.Value, unknown)
            || imported.CompanionCaptureOwner.IsPresent || imported.Type.Value != new TypeNode.Fqn("Imported.Carrier")
            || suspend.CompanionCaptureOwner.IsPresent || markedClass.CompanionCaptureOwner.IsPresent || markedNew.CompanionCaptureOwner.IsPresent
            || descriptor.Type.Value != new TypeNode.Nullable(new TypeNode.Fqn(Physical))
            || descriptor.KotlinType.Value != new TypeNode.Fqn(Semantic) // Opaque saved source facts are not rewritten.
            || capturedValue.Sty.Value != new TypeNode.Fqn(Physical) || suspend.CapValues.Value[1] != null
            || markedClass.Fields.Value[0].Type.Value != new TypeNode.Fqn(Physical)
            || markedNew.ArgTypes.Value[0] != new TypeNode.Fqn(Physical) || !Singleton(markedNew.Args.Value[0]))
            throw new InvalidOperationException("Typed companion use binding lost selected owners, capture slots, stores or owning identities");
        var stable = root.DeepClone();
        CompanionRepresentationLowering.BindUses(root, local, null, bindExternal: false);
        if (!root.StructurallyEquals(stable))
            throw new InvalidOperationException("Companion use binding changed an already-bound payload");
    }

    static void ExternalUses()
    {
        const string semantic = "Ref.Owner.Named";
        const string physical = "Ref.Owner+$Named";
        var refs = ReferenceMetadataIndex.CompanionUseTestIndex();
        var local = new CompanionRepresentationLowering.LocalIndex(Array.Empty<CompanionRepresentationLowering.Association>());
        var accessor = new BirExpressionStatement { Expr = new BirStaticFieldGet {
            OwnerType = new TypeNode.Fqn("Ref.Owner", new TypeNode[] { new TypeNode.Fqn("Ref.Argument") }), Name = "Named" } };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn(semantic), Method = "selected",
            DeclarationId = "ref-selected", Recv = new BirLocal { Name = "sharedReceiver" } };
        var bound = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn(semantic), CalleeOwner = new TypeNode.Fqn("Ref.Sentinel"),
            Recv = new BirLocal { Name = "sharedReceiver" } };
        var direct = new BirLateinitIsInitialized { OwnerType = new TypeNode.Fqn("Ref.Owner.$Named"),
            Recv = Value(semantic), LateinitDeclarationId = "selected" };
        var forwarder = new BirClrEventForwarder { OwnerType = new TypeNode.Fqn("Ref.Owner.$Named"), Recv = Value(semantic) };
        var descriptor = new BirCaptureDescriptor { Name = "companion", Type = new TypeNode.Fqn(semantic) };
        var captured = new BirLocal { Name = "captured", Sty = new TypeNode.Fqn(semantic) };
        var suspend = new BirNewSuspendLambda { CompanionCaptureOwner = "Ref.Owner.$Named",
            Captures = new[] { descriptor }, CapValues = new IBirExpression[] { captured, Value(semantic), null } };
        var sourceType = new TypeNode.Fqn(semantic, new TypeNode[] { new TypeNode.Fqn("Ref.Argument") });
        var field = new BirFieldDeclaration { Name = "slot", Type = new TypeNode.Nullable(sourceType) };
        var nullField = new BirFieldDeclaration { Name = "null", Type = OptionalField<TypeNode>.Null };
        var absentField = new BirFieldDeclaration { Name = "absent" };
        var root = new BirFileDeclaration { FileClass = "External", Fields = new[] { field, nullField, absentField },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "Ref.Forwarder", ClrEventForwarders = new[] { forwarder } } },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { accessor,
                new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = bound },
                new BirExpressionStatement { Expr = direct }, new BirExpressionStatement { Expr = suspend } } } } };
        CompanionRepresentationLowering.BindUses(root, local, refs, bindExternal: true);
        bool Singleton(IBirExpression expression) => expression is BirStaticFieldGet load
            && load.OwnerType.OrDefault() == new TypeNode.Fqn(physical) && load.Sty.OrDefault() == new TypeNode.Fqn(physical)
            && load.Name.OrDefault() == "$INSTANCE";
        if (!Singleton(accessor.Expr.Value) || call.OwnerType.Value != new TypeNode.Fqn(physical)
            || !call.CompanionCall.Value || call.DeclarationId.Value != "ref-selected"
            || call.Recv.Value is not BirLocal { Name.Value: "sharedReceiver" }
            || bound.OwnerType.Value != new TypeNode.Fqn(physical) || bound.CalleeOwner.Value != new TypeNode.Fqn(physical)
            || !bound.CompanionCall.Value || !Singleton(direct.Recv.Value) || direct.OwnerType.Value != new TypeNode.Fqn(physical)
            || !Singleton(forwarder.Recv.Value) || forwarder.OwnerType.Value != new TypeNode.Fqn(physical)
            || suspend.CompanionCaptureOwner.IsPresent || descriptor.Type.Value != new TypeNode.Fqn(physical)
            || captured.Sty.Value != new TypeNode.Fqn(physical) || !Singleton(suspend.CapValues.Value[1]!)
            || suspend.CapValues.Value[2] != null || !nullField.Type.IsNull || absentField.Type.IsPresent
            || field.Type.Value is not TypeNode.Nullable { Of: TypeNode.Fqn { Name: physical, Args.Length: 1 } rewritten }
            || rewritten.Args[0] != new TypeNode.Fqn("Ref.Argument"))
            throw new InvalidOperationException("Typed external companion binding lost exact reference associations or capture/value facts");
        var stable = root.DeepClone();
        CompanionRepresentationLowering.BindUses(root, local, refs, bindExternal: true);
        if (!root.StructurallyEquals(stable))
            throw new InvalidOperationException("External companion binding changed an already-bound payload");
    }

    static void NestedPlacementOrder()
    {
        // The inner association appears first in source, but its enclosing generic companion must be placed first.
        var inner = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>.Child.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Outer.Named.Child", Name = "Named", Visibility = "public" } };
        var child = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>.Child", SemanticOwner = "Outer.<companion:Named>" };
        var outerCompanion = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Outer", Name = "Named", Visibility = "public" } };
        var outer = new BirClassDefinition { Kind = "class", Name = "Outer", TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var value = new BirExpressionStatement { Expr = new BirCompanionValue { CompanionType = new TypeNode.Fqn(inner.Name.Value) } };
        var root = new BirFileDeclaration { FileClass = "Nested", Types = new IBirTypeDeclaration[] { inner, child, outerCompanion, outer },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { value } } } };
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        if (!local.Associations.Select(a => a.SemanticName).SequenceEqual(new[] {
                "Outer.<companion:Named>", "Outer.<companion:Named>.Child.<companion:Named>" })
            || local.Associations[1].PhysicalOwner != "Outer$companion$Named+Child"
            || child.SemanticOwner.Value != "Outer$companion$Named"
            || value.Expr.Value is not BirStaticFieldGet { OwnerType.Value: TypeNode.Fqn { Name: "Outer.<companion:Named>.Child.$Named" } })
            throw new InvalidOperationException("Nested companion binding lost association order or exact physical ownership");
    }
}

// This is the same metadata/catalog construction used by ReferenceMetadataIndex.SelfTest, not a production mode.
sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex CompanionUseTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.CompanionPhysicalOwnerBySemanticType["Ref.Owner.Named"] = "Ref.Owner+$Named";
        metadata.CompanionCarrierByPhysicalOwner["Ref.Owner"] = "Ref.Owner+$Named";
        metadata.CompanionSourceNameByPhysicalOwner["Ref.Owner"] = "Named";
        return new ReferenceMetadataIndex(new List<ReferenceAssembly> { new("companion.dll", "companion", "1", metadata) },
            Build(Array.Empty<string>())._compileRefs);
    }
}
