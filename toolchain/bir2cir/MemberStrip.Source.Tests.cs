using System.Text.Json.Nodes;
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
        var methods = new[] { prefix, captured, retiredSibling, survivor, liveSibling, getter, obligation, incomplete };
        var native = new BirFieldDeclaration { Name = "native", Type = integer,
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrField") } } };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = methods, Fields = new[] { native },
            Properties = new[] { new BirPropertyDeclaration { Name = "value", PropertyAssociation = "selected" },
                new BirPropertyDeclaration { Name = "value", PropertyAssociation = "unrelated" } } };
        var ifaceMethod = Method("dropInterface");
        var iface = new BirInterfaceDefinition { Name = "Interface", Kind = "interface", Methods = new[] { ifaceMethod } };
        var source = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { owner, iface } };
        var metadata = new ReferenceDotKtMetadata();
        foreach (var name in new[] { "dropPrefix", "dropCaptured", "dropSibling", "dropGetter", "obligation" })
            metadata.MemberBindings.Add(new MemberBinding("Owner", name, 0, "bound", false, true, ParamTypeNodes: Array.Empty<TypeNode>()));
        metadata.MemberBindings.Add(new MemberBinding("Owner", "incomplete", 1, "bound", false, true, ParamTypeNodes: new TypeNode[] { integer }));
        metadata.MemberBindings.Add(new MemberBinding("Interface", "dropInterface", 0, "bound", false, true, ParamTypeNodes: Array.Empty<TypeNode>()));
        var refs = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        var roots = new[] { source };
        var values = ValueRepresentationLowering.CreateSource(roots, refs, false,
            new ValueRepresentationSourcePolicy(roots, refs, false), ValueRepresentationLowering.CaptureSource(roots), null);
        var generic = GenericRepresentationPolicy.CreateSource(roots, new Dictionary<string, string>(), refs);
        generic.PrepareDomLocations(roots);
        var pending = UncheckedGenericCastReturnErasure.CaptureSource(roots);
        UncheckedGenericCastReturnErasure.Apply(source, pending);
        var retired = new HashSet<BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        var reports = new List<BirFunctionDeclaration>();
        Apply(source, refs, method => { values.RetireSourceDeclaration(method); retired.Add(method); reports.Add(method); });
        if (!reports.SequenceEqual(new[] { getter, retiredSibling, captured, prefix })
            || !owner.Methods.Value.SequenceEqual(new[] { survivor, liveSibling, obligation, incomplete })
            || !ReferenceEquals(methods[0], prefix) || !ReferenceEquals(methods[1], captured)
            || !ReferenceEquals(source.Types.Value[0], owner) || !ReferenceEquals(owner.Fields.Value[0], native)
            || owner.Properties.Value.Length != 1 || owner.Properties.Value[0].PropertyAssociation.Value != "unrelated"
            || !ReferenceEquals(iface.Methods.Value[0], ifaceMethod))
            throw new InvalidOperationException("Typed MemberStrip lost exact retirements, source vectors, property association or exclusions");
        var continuing = source.ToDom();
        var detached = new Dictionary<BirFunctionDeclaration, JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var method in retired) detached.Add(method, method.ToDom());
        generic.BindDomDeclarations(new JsonNode[] { continuing });
        values.BindDomDeclarations(new JsonNode[] { continuing }, detached);
        var uncheckedIndex = UncheckedGenericCastReturnErasure.BindDomDeclarations(pending, new[] { continuing }, detached);
        if (!ReferenceEquals(uncheckedIndex.ByIdentity["captured"].Method, detached[captured])
            || !ReferenceEquals(uncheckedIndex.ByIdentity["survivor"].Method, continuing["types"][0]["methods"][0])
            || detached[captured].Parent != null || !generic.IsNativeField(native))
            throw new InvalidOperationException("MemberStrip retirement did not preserve the existing exact one-way declaration handoff");
        Console.WriteLine("[typed member strip] self-test OK (bound selection, exact retirement, ordinal shifts, same-ID sibling, native storage and associations)");
    }
}
