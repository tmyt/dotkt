using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class MemberStrip
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        BirFunctionDeclaration Method(string name, string id = null, bool captured = false) => new() {
            Name = name, DeclarationId = id == null ? default : id, Static = true,
            Params = Array.Empty<BirParameter>(), Ret = captured ? new TypeNode.Tv("method", 0) : text,
            TypeParams = captured ? new[] { new BirTypeParameter { Name = "T" } } : Array.Empty<BirTypeParameter>(),
            Body = captured ? new IBirStatement[] { new BirReturn { Value = new BirCast {
                Type = new TypeNode.Tv("method", 0), E = new BirLocal { Name = "value", Sty = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) },
            } } } : Array.Empty<IBirStatement>(),
        };
        var prefix = Method("dropPrefix");
        var captured = Method("dropCaptured", "captured", true);
        var retiredSibling = Method("dropSibling", "shared");
        var survivor = Method("keepCaptured", "survivor", true);
        var liveSibling = Method("keepSibling", "shared");
        var getter = Method("dropGetter", "getter");
        getter.PropertyName = "value"; getter.PropertyAccessor = "get"; getter.PropertyAssociation = "selected";
        var obligation = Method("obligation"); obligation.Abstract = true;
        var incomplete = Method("incomplete"); incomplete.Params = new[] { new BirParameter { Name = "untyped" } };
        var methods = new[] { prefix, captured, survivor, liveSibling, getter, obligation, incomplete };
        var native = new BirFieldDeclaration { Name = "native", Type = integer,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = methods, Fields = new[] { native },
            Properties = new[] { new BirPropertyDeclaration { Name = "value", PropertyAssociation = "selected" },
                new BirPropertyDeclaration { Name = "value", PropertyAssociation = "unrelated" } } };
        // Repeated source IDs occur on distinct declaration owners, not twice in one owner catalog.
        var siblingOwner = new BirClassDefinition { Name = "SiblingOwner", Kind = "class", Methods = new[] { retiredSibling } };
        var ifaceMethod = Method("dropInterface");
        var iface = new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Methods = new[] { ifaceMethod } };
        var source = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { siblingOwner, owner, iface } };
        var metadata = new ReferenceDotKtMetadata();
        foreach (var name in new[] { "dropPrefix", "dropCaptured", "dropGetter", "obligation" })
            metadata.MemberBindings.Add(new MemberBinding("Owner", name, 0, "bound", false, true, ParamTypeNodes: Array.Empty<TypeNode>()));
        metadata.MemberBindings.Add(new MemberBinding("SiblingOwner", "dropSibling", 0, "bound", false, true, ParamTypeNodes: Array.Empty<TypeNode>()));
        metadata.MemberBindings.Add(new MemberBinding("Owner", "incomplete", 1, "bound", false, true, ParamTypeNodes: new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }));
        metadata.MemberBindings.Add(new MemberBinding("Interface", "dropInterface", 0, "bound", false, true, ParamTypeNodes: Array.Empty<TypeNode>()));
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var roots = new[] { source };
        var values = ValueRepresentationLowering.CreateSource(roots, refs, false,
            new ValueRepresentationSourcePolicy(roots, refs, false), ValueRepresentationLowering.CaptureSource(roots), null);
        var generic = GenericRepresentationPolicy.CreateSource(roots, new Dictionary<string, string>(), refs);
        generic.PrepareSourceLocations(roots);
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
        UncheckedGenericCastReturnErasure.Apply(source, pending);
        var capturedFacts = pending.SourceDeclarations.ByIdentity["captured"];
        var survivorFacts = pending.SourceDeclarations.ByIdentity["survivor"];
        var bindings = new CurrentDeclarationBindings(roots);
        var declarations = new SourceDeclarationProjection(roots, bindings);
        var reports = new List<BirFunctionDeclaration>();
        Apply(source, refs, method => { values.RetireSourceDeclaration(method); declarations.RetireForest(method); reports.Add(method); });
        if (!reports.SequenceEqual(new[] { retiredSibling, getter, captured, prefix })
            || !owner.Methods.Value.SequenceEqual(new[] { survivor, liveSibling, obligation, incomplete })
            || !ReferenceEquals(methods[0], prefix) || !ReferenceEquals(methods[1], captured)
            || !ReferenceEquals(source.Types.Value[1], owner) || !ReferenceEquals(owner.Fields.Value[0], native)
            || siblingOwner.Methods.Value.Length != 0
            || owner.Properties.Value.Length != 1 || owner.Properties.Value[0].PropertyAssociation.Value != "unrelated"
            || !ReferenceEquals(iface.Methods.Value[0], ifaceMethod))
            throw new InvalidOperationException("Typed MemberStrip lost exact retirements, source vectors, property association or exclusions");
        declarations.SealSourceContinuations();
        generic.CompleteSourceContinuation(roots, null, declarations);
        values.CompleteSourceContinuation(roots, declarations);
        UncheckedGenericCastReturnErasure.CompleteSourceDeclarations(pending, roots, null, declarations);
        if (!declarations.IsRetired(captured) || declarations.IsRetired(survivor)
            || !ReferenceEquals(owner.Methods.Value[0], survivor)
            || capturedFacts.SourceMethod != null || survivorFacts.SourceMethod != null
            || capturedFacts.Return != new TypeNode.Tv("method", 0) || survivorFacts.Return != capturedFacts.Return
            || !generic.IsNativeField(native))
            throw new InvalidOperationException("MemberStrip retirement changed exact actual continuation or original body-return facts");
        bindings.CompleteSourceCustody(declarations);
        declarations.Release();
        Console.WriteLine("[typed member strip] self-test OK (bound selection, exact retirement, ordinal shifts, same-ID sibling, native storage and associations)");
    }
}
