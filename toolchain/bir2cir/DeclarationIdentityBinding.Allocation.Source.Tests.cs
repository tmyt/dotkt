using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationIdentityBinding
{
    internal static void LocalAllocationSourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning declaration allocation: " + message); }
        static TypeNode.Fqn Named(string name) => new(name);
        BirFunctionDeclaration Method(string id, string name = "read", TypeNode parameter = null) => new() {
            Name = name, DeclarationSourceName = name, DeclarationId = id, Ret = Named("void"),
            Params = parameter == null ? Array.Empty<BirParameter>() : new[] { new BirParameter { Type = parameter } },
        };
        void Refused(Action action, string expected)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Expected declaration allocation refusal: " + expected);
        }
        var authored = Method("authored") with { ExplicitClrName = "selected" };
        var physical = Method(PhysicalOnlyId("authored", "wrapper"));
        var existential = Method("authored" + ExistentialSlotMarker + "slot");
        var root = new BirFileDeclaration { FileClass = "pkg.Facade", Methods = new[] { authored },
            Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "Physical", Generated = true,
                Methods = new[] { physical, existential with { Name = "separate" } } } } };
        var names = AllocatePhysicalNames(new[] { root }, out var ids);
        Require(names["authored"] == "selected" && names[physical.DeclarationId.Value] == "selected"
            && names[existential.DeclarationId.Value] == "separate"
            && ids.SetEquals(new[] { physical.DeclarationId.Value, existential.DeclarationId.Value }),
            "explicit source-name propagation or separate generated naming domain changed");
        var virtualMethod = Method("virtual", "echo") with { Virtual = true };
        var owner = new BirClassDefinition { Name = "VirtualOwner", Kind = "class", Methods = new[] { virtualMethod } };
        var family = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        Require(AllocatePhysicalNames(new[] { family }, out _)["virtual"] == "echo", "open physical family was renamed");
        virtualMethod.ExplicitClrName = "renamed";
        Refused(() => AllocatePhysicalNames(new[] { family }, out _), "independent physical-name allocation");
        family.Types = new IBirTypeDeclaration[] { new BirInterfaceDefinition {
            Name = "VirtualOwner", Kind = "interface", Methods = new[] { virtualMethod } } };
        Require(AllocatePhysicalNames(new[] { family }, out _)["virtual"] == "renamed", "concrete interface independent name was refused");
        virtualMethod.Abstract = true;
        Refused(() => AllocatePhysicalNames(new[] { family }, out _), "independent physical-name allocation");

        var first = Method("first", parameter: Named("System.String"));
        var second = Method("second", parameter: Named("System.Int32"));
        var sameShape = new BirFileDeclaration { FileClass = "pkg.First", Methods = new[] { first, second } };
        AllocatePhysicalNames(new[] { sameShape }, out var sameFamily);
        Require(sameFamily.SetEquals(new[] { "first", "second" }), "same-source scalar-shape carrier family disappeared");
        var cross = new BirFileDeclaration { FileClass = "pkg.Second", Methods = new[] { Method("other", parameter: Named("System.String")) } };
        AllocatePhysicalNames(new[] { new BirFileDeclaration { FileClass = "pkg.First", Methods = new[] { first } }, cross }, out var packageIds);
        Require(packageIds.SetEquals(new[] { "first", "other" }), "merged package/facade collision lost semantic carriers");
        Refused(() => AllocatePhysicalNames(new[] { new BirFileDeclaration { FileClass = "Collision", Methods = new[] {
            Method("left"), Method("right") } } }, out _), "automatic hash suffixes are not used");
        var generated = Method("generated") with { Generated = true };
        AllocatePhysicalNames(new[] { new BirFileDeclaration { FileClass = "Generated", Methods = new[] { generated, generated with { } } } }, out _);
        Refused(() => AllocatePhysicalNames(new[] { new BirFileDeclaration { Methods = new[] {
            Method("duplicate", "one"), Method("duplicate", "two") } } }, out _), "multiple physical names");
        foreach (var invalid in new[] { "", ".ctor", ".cctor", "bad\0name", "bad\uD800" })
            Refused(() => AllocatePhysicalNames(new[] { new BirFileDeclaration { Methods = new[] {
                Method("explicit") with { ExplicitClrName = invalid } } } }, out _), "malformed @ClrName");

        LocalUseSourceSelfTest();
        Console.WriteLine("[owning declaration allocation] self-test OK (names, families, facades, exact local uses and identity consumption)");
    }

    static void LocalUseSourceSelfTest()
    {
        static TypeNode.Fqn Named(string name) => new(name);
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning selected local use: " + message); }
        var id = "selected";
        var selected = new BirFunctionDeclaration { DeclarationId = id, Name = "source", Ret = new TypeNode.Fqn("Box",
            new TypeNode[] { new TypeNode.Tv("method", 1) }), TypeParams = new[] {
                new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "S" } },
            Params = new[] { new BirParameter { Type = Named("System.Object") },
                new BirParameter { Type = new TypeNode.Tv("method", 0) },
                new BirParameter { Type = new TypeNode.Mod(false, Named("Marker"), new TypeNode.Tv("method", 1)) } } };
        TypeNode[] Signature() => new TypeNode[] { Named("semantic.Receiver"), Named("System.String"), Named("System.Int32") };
        var receiver = new BirLocal { Name = "receiver" };
        var argument = new BirLocal { Name = "argument" };
        var arguments = new IBirExpression[] { argument, new BirLocal { Name = "second" }, new BirLocal { Name = "third" } };
        var stat = new BirCallStatic { DeclarationId = id, Sig = Signature(), Prop = "get", Args = arguments };
        var instance = new BirCallInstance { DeclarationId = id, Sig = Signature(), Prop = "get", Recv = receiver };
        var constrained = new BirConstrainedCall { DeclarationId = id, Sig = Signature(), Prop = "get" };
        var unbound = new BirNewDelegate { DeclarationId = id, Sig = Signature() };
        var bound = new BirNewBoundDelegate { DeclarationId = id, Sig = Signature() };
        var handler = new TypeNode.Fn(false, Named("void"), Array.Empty<TypeNode>(), Clr: "System.Action");
        var eventMethod = new BirFunctionDeclaration { DeclarationId = "event", Name = "eventSource", Ret = Named("void"),
            Params = new[] { new BirParameter { Type = handler } } };
        var add = new BirEventAdd { DeclarationId = "event" };
        var remove = new BirEventRemove { DeclarationId = "event" };
        var attributeValue = new BirAttributeLiteral { Type = Named("System.String"), Value = new BirLiteral.TextLiteral("source") };
        var accessor = new BirFunctionDeclaration { Name = "unsafe", UnsafeTargetDeclarationId = id,
            Attrs = new[] { new BirAttribute { Attr = Named("System.Runtime.CompilerServices.UnsafeAccessorAttribute"),
                NamedArgs = new[] { new BirAttributeNamedArgument { Name = "Name",
                    Value = OptionalField<IBirAttributeValue>.Of(attributeValue) } } } } };
        var consumed = new BirFunctionDeclaration { Name = "physical", DeclarationSourceName = "logical",
            DeclarationId = PhysicalOnlyId("original", "bridge"), Ret = Named("void"),
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            NullableRepresentationFrame = new NullableRepresentationFrame(1, Array.Empty<int>()),
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = Array.Empty<TypeNode>() }),
            Params = Array.Empty<BirParameter>() };
        var slot = consumed with { DeclarationId = "original" + ExistentialSlotMarker + "slot", Name = "slot" };
        var cold = consumed with { DeclarationId = "original|cold", Name = "cold" };
        var alias = consumed with { DeclarationId = "original" + AliasHelperHoist.IdentitySuffix, Name = "alias" };
        var absent = consumed with { DeclarationId = "ordinary", Name = "ordinary", NullableRepresentationFrame = default,
            MethodTypeParameterBoundsPre = default, DeclarationSemanticSignature = OptionalField<SourceDeclarationSignature>.Null };
        var semantic = consumed with { DeclarationId = "semantic", Name = "semantic", NullableRepresentationFrame = default };
        var semanticFacts = new SourceDeclarationSignatures();
        semanticFacts.Capture(new[] { new BirFileDeclaration { Methods = new[] { semantic } } });
        var owner = new BirClassDefinition { Name = "Owner", Methods = new[] { consumed, slot, cold, alias, absent, semantic } };
        var expressions = new IBirExpression[] { stat, instance, constrained, unbound, bound, add, remove };
        var caller = new BirFunctionDeclaration { Name = "caller", Body = expressions.Select(expression =>
            (IBirStatement)new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray() };
        var root = new BirFileDeclaration { Methods = new[] { selected, eventMethod, accessor, caller }, Types = new IBirTypeDeclaration[] { owner } };
        var names = new Dictionary<string, string> { [id] = "physicalSelected", ["event"] = "physicalEvent" };
        foreach (var method in owner.Methods.Value) names.Add(method.DeclarationId.Value, method.Name.Value);
        var notices = new List<BirFunctionDeclaration>();
        ApplyLocal(new[] { root }, names, new HashSet<string> { "semantic" }, semanticFacts,
            new Dictionary<string, int[]> { [id] = new[] { 0 } }, null, (file, actualOwner, method, beforeId) => {
                Require(ReferenceEquals(file, root) && ReferenceEquals(actualOwner, owner)
                    && owner.Methods.Value.Contains(method, ReferenceEqualityComparer.Instance)
                    && method.DeclarationId.Value == beforeId && ReferenceEquals(method, consumed),
                    "ID consumption was not reported on the exact attached occurrence before clearing");
                notices.Add(method);
            });
        Require(notices.Count == 1 && !consumed.DeclarationId.IsPresent && !consumed.DeclarationSourceName.IsPresent
            && !consumed.NullableRepresentationFrame.IsPresent && !consumed.MethodTypeParameterBoundsPre.IsPresent
            && slot.DeclarationId.HasValue && !slot.NullableRepresentationFrame.IsPresent && cold.DeclarationId.HasValue
            && alias.DeclarationId.HasValue && !absent.DeclarationSemanticSignature.IsPresent
            && semantic.DeclarationSemanticSignature.HasValue,
            "physical-only/existential/cold/alias/source metadata branches changed");
        Require(ReferenceEquals(stat.Args.Value, arguments) && ReferenceEquals(arguments[0], argument)
            && ReferenceEquals(instance.Recv.Value, receiver) && !stat.Prop.IsPresent && !instance.Prop.IsPresent
            && !constrained.Prop.IsPresent && attributeValue.Value.Value is BirLiteral.TextLiteral { Value: "physicalSelected" }
            && !accessor.UnsafeTargetDeclarationId.IsPresent,
            "local binding copied an operand or lost property/unsafe selected identity");
        foreach (var expression in expressions)
            Require(!((BirGraphValue)expression).DeclarationIdentityFact.IsPresent, "a selected local use retained its ID");
        foreach (var (name, result, signature, parameters) in new[] {
            (stat.Method, stat.CalleeRet, stat.Sig, stat.CalleeParams),
            (instance.Method, instance.CalleeRet, instance.Sig, instance.CalleeParams),
            (constrained.Method, constrained.CalleeRet, constrained.Sig, constrained.CalleeParams),
            (unbound.Method, unbound.CalleeRet, unbound.Sig, unbound.CalleeParams),
            (bound.Method, bound.CalleeRet, bound.Sig, bound.CalleeParams) })
            Require(name.Value == "physicalSelected" && result.Value == selected.Ret.Value
                && signature.Value[0] == Named("System.Object") && signature.Value[1] == Named("System.String")
                && signature.Value[2] == Named("System.Int32") && parameters.Value[2] is TypeNode.Mod,
                "selected declaration return/discriminator or partial physical signature rewrite changed");
        Require(add.Accessor.Value == "physicalEvent" && remove.Accessor.Value == "physicalEvent"
            && add.Sig.Value[0] == handler && remove.Sig.Value[0] == handler,
            "event accessor lost its exact selected declaration signature");

        var unresolved = new BirCallStatic { DeclarationId = "missing" };
        try
        {
            ApplyLocal(new[] { new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = unresolved } } } } } },
                new Dictionary<string, string>(), new HashSet<string>(), semanticFacts, new Dictionary<string, int[]>(), null,
                (_, _, _, _) => { });
            throw new Exception("An unresolved current ID was silently accepted");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("unresolved frontend declaration identity", StringComparison.Ordinal)) { }
    }
}
