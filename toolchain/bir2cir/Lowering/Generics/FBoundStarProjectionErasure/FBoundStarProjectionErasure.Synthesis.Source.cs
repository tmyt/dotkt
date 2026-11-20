using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static void SynthesizeSource(Owner owner, CarrierRealizationContext context)
    {
        var root = context.Root(owner);
        var definition = context.Definition(owner);
        if (!root.Types.HasValue || root.Types.Value.Any(type => type.Name.OrDefault() == owner.ErasedName)) return;
        var inherited = new List<TypeNode>();
        AddErasedAncestorSource(definition.Base.OrDefault(), inherited, context, false);
        foreach (var type in definition.Interfaces.OrEmpty()) AddErasedAncestorSource(type, inherited, context, true);
        foreach (var (type, isInterface) in context.Demand.BaseContracts[owner.Name])
            AddErasedAncestorSource(type, inherited, context, isInterface);
        var locals = context.SourceDefinitions.Keys.ToHashSet(StringComparer.Ordinal);
        string PhysicalKey(TypeNode type) => TypeNode.ToJson(BirTypeLowering.CanonicalPhysicalSlotType(
            BirTypeLowering.LowerPhysicalType(type, context.Aliases, context.References.IsValueType,
                context.References.PhysicalTypeNames, typeArg: false, localTypeNames: locals,
                nullableFrames: context.References.NullableTypeFrames)));
        inherited = inherited.GroupBy(PhysicalKey, StringComparer.Ordinal)
            .Select(group => BirGraphValue.CopyType(group.First())).ToList();
        var methods = new List<BirFunctionDeclaration>();
        var methodProofSources = new Dictionary<BirFunctionDeclaration, BirFunctionDeclaration>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (definition.Methods.HasValue)
        {
            var originals = definition.Methods.Value.ToArray();
            var collisions = originals.Where(method => !method.Static.OrDefault())
                .GroupBy(SourceMethodKey).Where(group => group.Count() > 1).SelectMany(group => group).ToHashSet();
            foreach (var method in originals)
            {
                if (method.Static.OrDefault()) continue;
                var requiresBridge = ContainsOwnerTvInSourceSignature(method)
                    || method.Vis.OrDefault() is not (null or "public") || method.ExplicitClrName.HasValue
                    || collisions.Contains(method);
                var slot = InterfaceSlotSource(method, requiresBridge ? StarMethodNameSource(definition, method) : null,
                    owner.Name, context);
                var original = context.Bindings.GetCapturedSource(method);
                var row = new SourceSlot(original, slot, context.Surface.Signatures.GetSourceSignature(original));
                // Keep selection rows even for a duplicate proposal. Only a
                // slot actually attached below becomes an owning declaration.
                context.Declarations.Add(owner, row);
                var key = SourceMethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                if (CreateReferenceContractSource(owner, method.Params.OrEmpty(), method.DeclarationId,
                    slot, context) is { } reference)
                {
                    row.ReferenceContract = reference; row.EmittedSlot = slot;
                    row.EmittedBinding = reference.SlotBinding;
                    context.NativeProofs.TransferIndependentMethodProofs(method, slot);
                    var bridge = BridgeMethodSource(owner, method, context, slotTypeParams: slot.TypeParams.OrDefault(),
                        referenceContract: reference);
                    DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
                    context.AttachMethod(definition, bridge, method);
                    continue;
                }
                methods.Add(slot);
                methodProofSources.Add(slot, method);
                row.EmittedSlot = slot;
                if (requiresBridge)
                {
                    var bridge = BridgeMethodSource(owner, method, context, slotTypeParams: slot.TypeParams.OrDefault());
                    DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
                    context.AttachMethod(definition, bridge, method);
                }
                else if (definition.Kind.OrDefault() == "interface")
                {
                    var bridge = BridgeMethodSource(owner, method, context, StarMethodNameSource(definition, method),
                        slotMemberName: slot.ExplicitClrName.OrDefault() ?? slot.Name.OrDefault(),
                        slotTypeParams: slot.TypeParams.OrDefault());
                    DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
                    context.AttachMethod(definition, bridge, method);
                }
                else DeclarationSignatureIdentity.RecordSelectedImplementationSlot(method, slot);
            }
            foreach (var (declaringName, method) in InheritedConcreteBaseMethodsSource(owner, context.Demand))
            {
                if (method.TypeParams.OrEmpty().Any(parameter => parameter.Constraints.OrEmpty().Any(ContainsOwnerTv))) continue;
                var declaring = (IBirTypeDeclaration)context.SourceDefinitions[declaringName].Source;
                var bridgeName = BaseStarMethodNameSource(declaringName, declaring, method);
                var slot = InterfaceSlotSource(method, bridgeName, owner.Name, context);
                var key = SourceMethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                methodProofSources.Add(slot, method);
                var bridge = BridgeMethodSource(owner, method, context, bridgeName,
                    new TypeNode.Fqn(declaringName), slotTypeParams: slot.TypeParams.OrDefault());
                DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
                context.AttachMethod(definition, bridge, method);
            }
            foreach (var inner in context.Owners.Values.Where(candidate => candidate.Needed
                && candidate.SemanticOwner == owner.Name && candidate.Inner))
            {
                var ordinal = 0;
                foreach (var constructor in context.Definition(inner).Ctors.OrEmpty())
                {
                    var factory = InnerConstructorFactorySource(owner, inner, constructor, ordinal++, context);
                    var key = SourceMethodKey(factory.Slot);
                    if (key == null || !seen.Add(key)) throw new InvalidOperationException(
                        $"duplicate existential inner-constructor factory for '{inner.Name}'");
                    if (CreateReferenceContractSource(owner, factory.Bridge.Params.Value, constructor.DeclarationId,
                        factory.Slot, context) is { } reference)
                    {
                        factory.ReferenceContract = reference;
                        factory.Bridge.ClrInterfaceImpls.Value[0].Owner = BirGraphValue.CopyType(reference.Owner);
                    }
                    else methods.Add(factory.Slot);
                    factory.SlotBinding = factory.ReferenceContract?.SlotBinding;
                    context.Declarations.Add(owner, factory);
                    context.AttachMethod(definition, factory.Bridge);
                    factory.BridgeBinding = context.Bindings.GetCapturedSource(factory.Bridge);
                }
            }
        }
        var erased = new BirInterfaceDefinition {
            Name = owner.ErasedName, Kind = "interface", Generated = true,
            KotlinType = new TypeNode.Fqn(owner.Name, Enumerable.Range(0, owner.Arity)
                .Select(_ => (TypeNode)new TypeNode.Star()).ToArray()),
            Base = OptionalField<TypeNode>.Null, Interfaces = inherited.ToArray(),
            Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Methods = methods.ToArray(), Properties = Array.Empty<BirPropertyDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
        };
        // The physical generic and its existential interface denote one source classifier.
        // Give reflection the actual allocated token, not a convention for parsing CLR names.
        BirAttribute ClassifierIdentity() => KClassMemberBinding.Identity(owner.ErasedName, owner.Name, owner.Name, false);
        definition.Attrs = definition.Attrs.OrEmpty().Append(ClassifierIdentity()).ToArray();
        erased.Attrs = new[] { ClassifierIdentity() };
        RegisterProjectedStorage(owner, definition, erased, context);
        if (PublishedApiTypeVisibilityLowering.ResolveVisibility(definition) is { } visibility) erased.Vis = visibility;
        context.AttachType(root, erased);
        foreach (var slot in methods)
        {
            var binding = context.Bindings.GetCapturedSource(slot);
            var selected = context.Declarations.Slots(owner).Values.FirstOrDefault(row => ReferenceEquals(row.Slot, slot));
            if (selected != null) selected.EmittedBinding = binding;
            var factory = context.Declarations.InnerFactories(owner).Values.FirstOrDefault(row => ReferenceEquals(row.Slot, slot));
            if (factory != null) factory.SlotBinding = binding;
            // Inherited slots and constructor factories have no direct source
            // selection row here; their own declarations still remain actual.
            if (methodProofSources.TryGetValue(slot, out var source)) context.NativeProofs.TransferIndependentMethodProofs(source, slot);
        }
        if (!definition.Interfaces.OrEmpty().Any(type => type is TypeNode.Fqn named && named.Name == owner.ErasedName))
            definition.Interfaces = definition.Interfaces.OrEmpty().Append(new TypeNode.Fqn(owner.ErasedName)).ToArray();
    }

    static void AddErasedAncestorSource(TypeNode type, List<TypeNode> target, CarrierRealizationContext context, bool concrete)
    {
        if (type is not TypeNode.Fqn named) return;
        TypeNode.Fqn inherited;
        if (concrete && !ContainsStarOrTypeVariable(named)) inherited = named;
        else if (context.Owners.TryGetValue(named.Name, out var ancestor)) inherited = new(ancestor.ErasedName);
        else if (context.References.TryExistentialPhysicalOwner(named.Name, out var physical)) inherited = new(physical);
        else return;
        if (!target.Contains(inherited)) target.Add(BirGraphValue.CopyType(inherited));
    }

    static string SourceMethodKey(BirFunctionDeclaration method) => method.Name.OrDefault() is not string name ? null
        : name + "|" + method.TypeParams.OrEmpty().Length + "|"
          + string.Join(";", method.Params.OrEmpty().Select(parameter => TypeNode.ToJson(FunctionSignatureIdentity.SignatureType(parameter))))
          + "|" + KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(method.TypeParams.OrDefault(), Array.Empty<TypeNode>());

    static string StarMethodNameSource(IBirTypeDeclaration owner, BirFunctionDeclaration method) =>
        "$star$" + method.Name.OrDefault() + "$" + owner.Methods.OrEmpty().TakeWhile(candidate => !ReferenceEquals(candidate, method)).Count();
    static string BaseStarMethodNameSource(string name, IBirTypeDeclaration owner, BirFunctionDeclaration method) =>
        "$star$base$" + new string(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray())
        + "$" + method.Name.OrDefault() + "$" + owner.Methods.OrEmpty().TakeWhile(candidate => !ReferenceEquals(candidate, method)).Count();

    static IEnumerable<(string Name, BirFunctionDeclaration Method)> InheritedConcreteBaseMethodsSource(Owner owner,
        CarrierDemandPlan context)
    {
        var next = ((IBirTypeDeclaration)owner.DefinitionBinding.Source).Base.OrDefault() as TypeNode.Fqn;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next != null && seen.Add(next.Name))
        {
            if (context.Owners.ContainsKey(next.Name) || !context.Preparation.Bindings.SourceDefinitions.TryGetValue(next.Name, out var source)) yield break;
            var definition = (IBirTypeDeclaration)source.Source;
            foreach (var method in definition.Methods.OrEmpty())
                if (!method.Static.OrDefault() && method.Vis.OrDefault() != "private") yield return (next.Name, method);
            next = definition.Base.OrDefault() as TypeNode.Fqn;
        }
    }

    static bool ContainsOwnerTvInSourceSignature(BirFunctionDeclaration method) =>
        ContainsOwnerTv(method.NullableGenericRet.OrDefault()) || ContainsOwnerTv(method.NullableGenericSuspendRet.OrDefault())
        || ContainsOwnerTv(method.Ret.OrDefault()) || method.Params.OrEmpty().Any(parameter =>
            ContainsOwnerTv(parameter.NullableGeneric.OrDefault()) || ContainsOwnerTv(parameter.FunctionSignatureType.OrDefault())
            || ContainsOwnerTv(parameter.Type.OrDefault()))
        || method.TypeParams.OrEmpty().Any(parameter => parameter.Constraints.OrEmpty().Any(ContainsOwnerTv));
}
