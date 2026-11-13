using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Reflection;

static partial class ClrMemberResolution
{
    internal static void SelfTestTypedInterfaceSlots()
    {
        TypedInterfaceConstraintSelfTest();
        var text = new TypeNode.Fqn("System.String");
        var owner = new TypeNode.Fqn("Fixture.Interface", new TypeNode[] { new TypeNode.Fqn("System.Object"), text });
        var get = new BirFunctionDeclaration { Name = "get_Value", PropertyName = "Value", PropertyAccessor = "get",
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Tv("type", 1) };
        var method = new BirFunctionDeclaration { Name = "Convert", TypeParams = new[] { new BirTypeParameter {
                Name = "T", Constraints = new TypeNode[] { new TypeNode.Tv("type", 1) } } },
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 0) } }, Ret = new TypeNode.Tv("type", 1) };
        var excludedBridge = (BirFunctionDeclaration)get.DeepClone();
        excludedBridge.PhysicalSlotBridge = true;
        var excludedStatic = (BirFunctionDeclaration)get.DeepClone();
        excludedStatic.Static = true;
        var iface = new BirInterfaceDefinition { Name = "Fixture.Interface", Kind = "interface",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Outer" } }, TypeParams = new[] { new BirTypeParameter { Name = "Own" } },
            Methods = new[] { excludedBridge, excludedStatic, get, method } };
        var getter = new BirMethodImpl { Owner = owner, Member = "get_Value", Arity = 0, Params = Array.Empty<TypeNode>(), Ret = text };
        var generic = new BirMethodImpl { Owner = owner, Member = "Convert", Arity = 1,
            Params = new TypeNode[] { new TypeNode.Tv("method", 0) }, Ret = text,
            TypeParams = new[] { new BirTypeParameter { Name = "R", Constraints = new TypeNode[] { text } } } };
        var implementation = new BirFunctionDeclaration { Name = "implementation", Params = Array.Empty<BirParameter>(), Ret = text,
            ClrInterfaceImpls = new[] { getter, (BirMethodImpl)getter.DeepClone(), generic } };
        var cls = new BirClassDefinition { Name = "Fixture.Class", Kind = "class", Methods = new[] { implementation } };
        var prebound = new BirClassDefinition { Name = "Fixture.Prebound", Kind = "class", InterfaceSlotRefs = OptionalField<BirInterfaceSlotSet[]>.Null,
            Methods = new[] { new BirFunctionDeclaration { Name = "prebound", ClrInterfaceImpls = new[] {
                (BirMethodImpl)getter.DeepClone(), (BirMethodImpl)getter.DeepClone() } } } };
        var first = new BirFileDeclaration { FileClass = "Implementation", Types = new IBirTypeDeclaration[] { cls, prebound } };
        var second = new BirFileDeclaration { FileClass = "Declaration", Types = new IBirTypeDeclaration[] { iface } };
        var roots = new[] { first, second };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        ResolveInterfaceSlots(roots, refs);
        if (!ReferenceEquals(cls.Methods.Value[0], implementation) || !ReferenceEquals(implementation.ClrInterfaceImpls.Value[0], getter)
            || implementation.ClrInterfaceImpls.Value.Length != 2 || getter.Member.Value != "get_Value" || getter.Ret.Value != text
            || !ReferenceEquals(implementation.ClrInterfaceImpls.Value[1], generic) || generic.Ret.Value != text
            || generic.Params.Value[0] != new TypeNode.Tv("method", 0)
            || cls.InterfaceSlotRefs.IsPresent || !prebound.InterfaceSlotRefs.IsNull
            || prebound.Methods.Value[0].ClrInterfaceImpls.Value.Length != 2)
            throw new InvalidOperationException("Typed local interface slots lost exact physical names, generic frames, bridge exclusions, deduplication or marker presence");
        var stable = first.DeepClone();
        ResolveInterfaceSlots(roots, refs);
        if (!first.StructurallyEquals(stable)) throw new InvalidOperationException("Final typed local interface resolution was not stable");

        var external = new BirMethodImpl { Owner = new TypeNode.Fqn("System.Collections.Generic.IEnumerable", new TypeNode[] { text }),
            Member = "GetEnumerator", Arity = 0, Params = Array.Empty<TypeNode>(),
            Ret = new TypeNode.Fqn("System.Collections.Generic.IEnumerator", new TypeNode[] { text }) };
        var externalMethod = new BirFunctionDeclaration { Name = "GetEnumerator", ClrInterfaceImpls = new[] {
            external, (BirMethodImpl)external.DeepClone() } };
        var externalClass = new BirClassDefinition { Name = "Fixture.External", Kind = "class", Methods = new[] { externalMethod } };
        var externalRoot = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { externalClass } };
        var native = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        ResolveInterfaceSlots(new[] { externalRoot }, native);
        var set = externalClass.InterfaceSlotRefs.Value.Single();
        var slot = set.Slots.Value.Single();
        if (!ReferenceEquals(externalMethod.ClrInterfaceImpls.Value.Single(), external)
            || slot.Name.Value != "GetEnumerator" || !slot.ParameterTypes.HasValue || slot.ParameterTypes.Value.Length != 0
            || slot.DeclaringType.Value is not TypeNode.Fqn { Args.Length: 1 } declaration
            || declaration.Args[0] != text || string.IsNullOrEmpty(set.Assembly.Value)
            || external.Ret.Value is not TypeNode.Fqn { Args.Length: 1 } result || result.Args[0] != text)
            throw new InvalidOperationException("Typed external interface slots lost exact selected references or constructed declaring frames");
        Console.WriteLine("[typed interface slots] self-test OK (cross-file local frames, physical accessors, exclusions, external metadata, deduplication)");
    }

    static void TypedInterfaceConstraintProbe<T>() where T : class, IComparable<T>, new() { }

    static void TypedInterfaceConstraintSelfTest()
    {
        var generic = typeof(ClrMemberResolution).GetMethod(nameof(TypedInterfaceConstraintProbe), BindingFlags.Static | BindingFlags.NonPublic)
            .GetGenericArguments().Single();
        var reflected = ReferenceMetadataIndex.GenericParamSourceDeclaration(generic);
        var descriptor = new BirTypeParameter { Name = "DifferentSourceName",
            Constraints = new TypeNode[] { new TypeNode.Fqn("System.IComparable", new TypeNode[] { new TypeNode.Tv("method", 0) }) },
            SpecialConstraints = new[] { "class", "new" } };
        var reflectedShape = KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(new[] { reflected }, Array.Empty<TypeNode>());
        if (reflectedShape != KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(new[] { descriptor }, Array.Empty<TypeNode>())
            || reflected.Variance.IsPresent
            || !reflected.SpecialConstraints.Value.SequenceEqual(new[] { "class", "new" }))
            throw new InvalidOperationException("Typed reflected interface constraints lost class/new or recursive method-frame facts");
        descriptor.SpecialConstraints = new[] { "class" };
        if (reflectedShape == KotlinOverrideSlotBridge.SourceMethodTypeParameterShapeKey(new[] { descriptor }, Array.Empty<TypeNode>()))
            throw new InvalidOperationException("Interface constraint selection ignored a reflected new() obligation");
        // Existing DOM consumers project exactly these same facts with their historical key order and presence.
        var metadata = ReferenceMetadataIndex.GenericParamDeclaration(generic).AsObject();
        if (!metadata.Select(pair => pair.Key).SequenceEqual(new[] { "name", "constraints", "specialConstraints" }))
            throw new InvalidOperationException("Reflected constraint metadata changed its existing order or optional-field presence");
    }
}
