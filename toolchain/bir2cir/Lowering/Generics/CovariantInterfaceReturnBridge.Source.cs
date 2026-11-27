using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CovariantInterfaceReturnBridge
{
    internal readonly record struct SourceBridgedSlot(BirFunctionDeclaration Implementation, string Descriptor);
    internal sealed class SourceSlotComparer : IEqualityComparer<SourceBridgedSlot>
    {
        public bool Equals(SourceBridgedSlot left, SourceBridgedSlot right)
            => ReferenceEquals(left.Implementation, right.Implementation) && left.Descriptor == right.Descriptor;
        public int GetHashCode(SourceBridgedSlot value) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Implementation), value.Descriptor);
    }

    sealed class SourceDefinition(BirFileDeclaration file, IBirTypeDeclaration declaration, string carrier)
    {
        internal BirFileDeclaration File => file;
        internal string Carrier => carrier;
        internal IBirTypeDeclaration Declaration => declaration;
        internal string Name => declaration.Name.OrDefault();
        internal string Kind => declaration.Kind.OrDefault();
        internal int Arity { get; } = declaration.TypeParams.OrEmpty().Length;
        internal TypeNode.Fqn[] Interfaces { get; } = declaration.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray();
        internal BirFunctionDeclaration[] Methods => declaration.Methods.OrEmpty();
        internal TypeNode[] OwnArguments => Enumerable.Range(0, Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
    }

    internal static IReadOnlySet<SourceBridgedSlot> ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, GenericRepresentationPolicy representations,
        IReadOnlyDictionary<BirFunctionDeclaration, TypeNode[]> sourceParameters,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration> onMaterialized = null, bool metadataBuild = false,
        IReadOnlyDictionary<string, string> localCarriers = null)
    {
        // The late hierarchy is last-declaration-wins, with the actual flat
        // file inventory. Appended adapters do not enter this captured view.
        var definitions = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        foreach (var file in roots)
            foreach (var declaration in file.Types.OrEmpty())
                if (declaration.Name.HasValue) definitions[declaration.Name.Value] = new(file, declaration,
                    localCarriers?.GetValueOrDefault(declaration.Name.Value));
        var bridged = new HashSet<SourceBridgedSlot>(new SourceSlotComparer());
        var hierarchy = new Lazy<Dictionary<string, SupertypeGraph.Def>>(() => SupertypeGraph.CollectSource(roots));
        foreach (var owner in definitions.Values.Where(value => value.Kind is "class" or "interface"))
            ApplySourceOwner(owner, definitions, refs, isValue, representations, sourceParameters, bridged, onMaterialized, metadataBuild, hierarchy);
        return bridged;
    }

    static bool SourceSuspend(BirFunctionDeclaration method) => method.Mods.OrDefault()?.Suspend.OrDefault() == true;
    static TypeNode SourceReturn(BirFunctionDeclaration method) => SourceSuspend(method) ? method.SuspendRet.OrDefault() : method.Ret.OrDefault();
    static TypeNode[] SourceParameterTypes(BirFunctionDeclaration method) => method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()).ToArray();

    static void ApplySourceOwner(SourceDefinition owner, IReadOnlyDictionary<string, SourceDefinition> definitions,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, GenericRepresentationPolicy representations,
        IReadOnlyDictionary<BirFunctionDeclaration, TypeNode[]> sourceParameters, ISet<SourceBridgedSlot> bridged,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration> onMaterialized, bool metadataBuild,
        Lazy<Dictionary<string, SupertypeGraph.Def>> hierarchy)
    {
        if (!owner.Declaration.Methods.HasValue) return;
        var bridges = new Dictionary<SourceBridgedSlot, BirFunctionDeclaration>(new SourceSlotComparer());
        var ordinal = 0;
        foreach (var application in ReachableSourceInterfaces(owner, definitions))
        {
            if (!definitions.TryGetValue(application.Name, out var declaration) || declaration.Kind != "interface") continue;
            var arguments = EffectiveArgs(application, declaration.Arity);
            if (arguments == null) continue;
            foreach (var slot in declaration.Methods)
            {
                if (slot.Static.OrDefault() || slot.PhysicalSlotBridge.OrDefault() || !slot.Name.HasValue || !slot.Params.HasValue) continue;
                var suspend = SourceSuspend(slot);
                var arity = slot.TypeParams.OrEmpty().Length;
                var slotParameters = SourceParameterTypes(slot).Select(type => type == null ? null : SubstOwnerTvs(type, arguments)).ToArray();
                var slotReturnOriginal = SourceReturn(slot);
                var slotReturn = slotReturnOriginal == null ? null : SubstOwnerTvs(slotReturnOriginal, arguments);
                if (slotParameters.Any(type => type == null) || slotReturn == null) continue;
                var valueReturn = IsUnit(slotReturn) && (!IsUnit(slotReturnOriginal) || slot.DotktValueReturn.OrDefault());
                KotlinPropertyAccessors.TryIdentity(slot, out var property, out var accessor);
                var candidates = owner.Methods.Where(method => !method.Static.OrDefault()
                    && !(owner.Kind == "interface" && method.Abstract.OrDefault()) && !method.PhysicalSlotBridge.OrDefault()
                    && SourceSuspend(method) == suspend && SameSourceIdentity(method, slot.Name.Value, property, accessor)
                    && method.TypeParams.OrEmpty().Length == arity
                    && KotlinOverrideSlotBridge.SameSourceMethodTypeParameterShape(slot.TypeParams.OrDefault(),
                        method.TypeParams.OrDefault(), arguments, owner.OwnArguments)
                    && method.Params.HasValue && ParametersEqual(SourceParameterTypes(method), slotParameters, owner.OwnArguments)
                    && SourceOverrides(method, slot.DeclarationId.OrDefault(), declaration.Name, property ?? slot.Name.Value,
                        accessor switch { "get" => "getter", "set" => "setter", _ => "method" })).ToArray();
                if (candidates.Length != 1) continue;
                var implementation = candidates[0];
                if (SourceReturn(implementation) is not { } implementationReturn) continue;
                implementationReturn = SubstOwnerTvs(implementationReturn, owner.OwnArguments);
                if (implementationReturn == slotReturn || KotlinOverrideSlotBridge.IsErasureDivergence(slotReturn, implementationReturn)) continue;
                // The slot is a declaration signature, not the storage type of its arguments.
                // Allocate any function discriminator in the declaration's open frame before closing it.
                var signature = slot.Params.Value.Select(FunctionSignatureIdentity.SignatureType)
                    .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, arguments)).ToArray();
                var key = SourceBridgeKey(implementation, arity,
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(slot.TypeParams.OrDefault(), arguments),
                    signature, slotReturn, valueReturn, refs, isValue);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    var logicalResult = suspend ? SubstOwnerTvs(slot.SuspendResult.OrDefault()
                        ?? throw new InvalidOperationException($"suspend declaration '{slot.Name.OrDefault()}' has no preserved logical result"), arguments) : null;
                    bridge = BuildSourceBridge(owner, implementation, slotParameters, slotReturn, logicalResult,
                        $"dotkt$covar${SafeName(slot.Name.Value)}${ordinal++}", valueReturn, signature);
                    bridges[key] = bridge;
                    PublishSourceBridge(owner, bridge, onMaterialized);
                    AssociateSourceProperty(owner, bridge, implementation, property, accessor, slotParameters, slotReturn);
                }
                var descriptor = SourceDescriptor(application, slot.Name.Value,
                    arity, signature, slotReturn, KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(
                        slot.TypeParams.OrDefault(), arguments), valueReturn);
                if (slot.DeclarationId.HasValue) descriptor.LocalOverrideTargets = new[] { slot.DeclarationId.Value };
                KotlinOverrideSlotBridge.AddSourceImpl(bridge, descriptor, true);
                bridged.Add(new(implementation, DescriptorKey(application, slot.Name.Value, arity, slotParameters, slotReturn, refs, isValue)));
            }
        }
        ApplySourceReferenced(owner, definitions, refs, isValue, representations, sourceParameters,
            bridges, bridged, ref ordinal, onMaterialized, metadataBuild, hierarchy);
    }

    static void PublishSourceBridge(SourceDefinition owner, BirFunctionDeclaration bridge,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration> onMaterialized)
    {
        owner.Declaration.Methods = owner.Methods.Append(bridge).ToArray();
        onMaterialized?.Invoke(owner.File, owner.Declaration, bridge);
    }

    static void AssociateSourceProperty(SourceDefinition owner, BirFunctionDeclaration bridge,
        BirFunctionDeclaration implementation, string property, string accessor, TypeNode[] parameters, TypeNode result)
    {
        if (property == null) return;
        var association = implementation.PropertyAssociation.OrDefault();
        if (owner.Kind == "interface") KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(bridge, property, accessor, association);
        else KotlinPropertyAccessors.AssociateBridgeProperty(owner.Declaration, bridge, property, accessor, association, parameters, result);
    }

    static void ApplySourceReferenced(SourceDefinition owner, IReadOnlyDictionary<string, SourceDefinition> definitions,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, GenericRepresentationPolicy representations,
        IReadOnlyDictionary<BirFunctionDeclaration, TypeNode[]> sourceParameters,
        Dictionary<SourceBridgedSlot, BirFunctionDeclaration> bridges, ISet<SourceBridgedSlot> bridged, ref int ordinal,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration> onMaterialized, bool metadataBuild,
        Lazy<Dictionary<string, SupertypeGraph.Def>> hierarchy)
    {
        if (refs == null) return;
        var ownArguments = owner.OwnArguments;
        var frames = refs.NullableTypeFrames.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var declaration in definitions.Values)
            if (NullableRepresentationViews.TypeFacts((BirGraphValue)declaration.Declaration)?.NullableFrame is { } frame)
                frames[declaration.Name] = frame;
        var mapping = new NullableRepresentationTypes(NullableRepresentationViews.TypeFacts((BirGraphValue)owner.Declaration)?.NullableFrame,
            null, frames, isValue, policy: representations);
        foreach (var implementation in owner.Methods.ToArray())
        {
            if (implementation.Static.OrDefault() || owner.Kind == "interface" && implementation.Abstract.OrDefault()
                || implementation.PhysicalSlotBridge.OrDefault() || !implementation.Name.HasValue || !implementation.Params.HasValue
                || !implementation.Overrides.HasValue || SourceReturn(implementation) is not { } implementationReturnOriginal) continue;
            var parameters = sourceParameters.TryGetValue(implementation, out var captured)
                ? captured : SourceParameterTypes(implementation);
            if (parameters.Length != implementation.Params.Value.Length || parameters.Any(type => type == null)) continue;
            var arity = implementation.TypeParams.OrEmpty().Length;
            var suspend = SourceSuspend(implementation);
            foreach (var edge in implementation.Overrides.Value)
            {
                if (edge.Owner.OrDefault() is not TypeNode.Fqn semanticOwner || definitions.ContainsKey(semanticOwner.Name)
                    || !edge.Member.HasValue) continue;
                // Native metadata MethodImpls have no forwarding body; the final slot pass binds
                // them in the emitted owner's frame, including a covariant return.
                if (metadataBuild && !refs.HasDotKtOwner(semanticOwner.Name)) continue;
                var sourceMember = edge.Member.Value;
                var ownerArguments = semanticOwner.Args ?? Array.Empty<TypeNode>();
                var projectedOwner = BirTypeLowering.LowerPhysicalType(mapping.Slot(semanticOwner), refs.Aliases, isValue,
                    refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames) as TypeNode.Fqn;
                if (projectedOwner == null || !refs.TryReferenceTypeShape(projectedOwner, out var ownerArity, out var kind, out _, out _)
                    || kind != "interface") continue;
                var physicalArguments = projectedOwner.Args ?? Array.Empty<TypeNode>();
                if (ownerArity != physicalArguments.Length) continue;
                var accessor = edge.Kind.OrDefault() switch { "getter" => "get", "setter" => "set", _ => null };
                if (accessor == "set") continue;
                // This is the actual post-erasure selector, not the earlier
                // semantic-constraint declaration-materialization overload.
                if (!refs.TrySelectedOverrideDeclarationByShape(semanticOwner.Name, sourceMember, accessor, arity,
                        parameters, ownerArguments, KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                            implementation.TypeParams.OrDefault(), ownArguments, refs.PhysicalConstraintType),
                        suspend, out var declaration, semanticConstraints: false, selectedDeclarationId: null)) continue;
                var declarationParameters = declaration.TypeParams;
                var slotParameters = declaration.PhysicalParameters.Select(type => SupertypeGraph.SubstOwnerTvs(type, physicalArguments)).ToArray();
                var declarationMapping = new NullableRepresentationTypes(refs.NullableTypeFrames.GetValueOrDefault(semanticOwner.Name),
                    declaration.NullableFrame, refs.NullableTypeFrames, isValue, policy: representations);
                var slotReturn = SupertypeGraph.SubstOwnerTvs(suspend
                    ? NullableGenericErasure.EraseNullableTv(declarationMapping.Slot(declaration.Return), isValue)
                    : declaration.PhysicalReturn, physicalArguments);
                var logicalResult = suspend ? SupertypeGraph.SubstOwnerTvs(declaration.Return, ownerArguments) : null;
                var valueReturn = IsUnit(slotReturn) && (suspend ? !IsUnit(declaration.Return) : declaration.ReturnsValue);
                var physicalOwner = refs.ExactReflectedOwner(semanticOwner.Name, ownerArity);
                if (physicalOwner == null) continue;
                var descriptorOwner = new TypeNode.Fqn(physicalOwner, physicalArguments.Length == 0 ? null : physicalArguments);
                var descriptorMember = declaration.PhysicalMember;
                if (refs.TryResolveClrOwner(semanticOwner.Name, out _, out _))
                {
                    if (accessor != null)
                    {
                        if (!refs.TryExternalPropertyAccessor(semanticOwner.Name, sourceMember, accessor, parameters.Length, arity,
                                parameters, ownerArguments, out _, out _, out descriptorMember)) continue;
                    }
                    else if (refs.TryExactMemberIntrinsic(semanticOwner.Name, sourceMember, arity, parameters, ownerArguments, out var intrinsic))
                        descriptorMember = intrinsic;
                    var comparable = slotParameters.Select(type => BirTypeLowering.LowerPhysicalType(type, refs.Aliases,
                        isValue, refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames)).ToArray();
                    if (!ClrMemberResolution.TryResolveAliasedInterfaceSlotByShape(null, refs, descriptorOwner, descriptorMember, arity,
                            comparable, declarationParameters == null ? null : KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                                declarationParameters, physicalArguments), out descriptorOwner, out descriptorMember,
                            out slotParameters, out slotReturn)) continue;
                    var referenceReturn = SupertypeGraph.SubstOwnerTvs(declaration.PhysicalReturn, physicalArguments);
                    if (!BirTypeLowering.SamePhysicalSlotType(referenceReturn, slotReturn, refs.Aliases, isValue,
                            refs.PhysicalTypeNames, returnPosition: true, nullableFrames: refs.NullableTypeFrames)) continue;
                }
                if (slotParameters.Any(type => type == null) || slotReturn == null
                    || !ParametersPhysicallyEqual(SourceParameterTypes(implementation), slotParameters, ownArguments, refs, isValue)) continue;
                var implementationReturn = SubstOwnerTvs(implementationReturnOriginal, ownArguments);
                if (implementationReturn == slotReturn || BirTypeLowering.SamePhysicalSlotType(slotReturn, implementationReturn,
                        refs.Aliases, isValue, refs.PhysicalTypeNames, returnPosition: true,
                        nullableFrames: refs.NullableTypeFrames, leftReturnsValue: !suspend && declaration.ReturnsValue)
                    || KotlinOverrideSlotBridge.IsErasureDivergence(slotReturn, implementationReturn)) continue;
                TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValue,
                    refs.PhysicalTypeNames, typeArg: false, nullableFrames: refs.NullableTypeFrames);
                if (owner.Kind == "class" && hierarchy.Value.TryGetValue(owner.Name, out var cls)
                    && KotlinOverrideSlotBridge.HasNativeBaseMapping(cls, hierarchy.Value, refs, Physical,
                        descriptorOwner, descriptorMember, arity, slotParameters, slotReturn)
                    && !KotlinOverrideSlotBridge.ReachesFromDeclaredPhysicalInterface(KotlinOverrideSlotBridge.DeclaredFaces(cls, owner.Carrier), descriptorOwner,
                        hierarchy.Value, refs, isValue, null)) continue;
                var key = SourceBridgeKey(implementation, arity,
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(declarationParameters, physicalArguments),
                    slotParameters, slotReturn, valueReturn, refs, isValue);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    bridge = BuildSourceBridge(owner, implementation, slotParameters, slotReturn, logicalResult,
                        $"dotkt$covar${SafeName(implementation.Name.Value)}${ordinal++}", valueReturn);
                    bridges[key] = bridge;
                    PublishSourceBridge(owner, bridge, onMaterialized);
                    AssociateSourceProperty(owner, bridge, implementation, accessor == null ? null : sourceMember, accessor, slotParameters, slotReturn);
                }
                var descriptor = SourceDescriptor(descriptorOwner, descriptorMember, arity, slotParameters, slotReturn,
                    KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(declarationParameters, physicalArguments), valueReturn);
                KotlinOverrideSlotBridge.AddSourceImpl(bridge, descriptor, true);
                bridged.Add(new(implementation, DescriptorKey(descriptorOwner, descriptorMember, arity, slotParameters, slotReturn, refs, isValue)));
            }
        }
    }

    static SourceBridgedSlot SourceBridgeKey(BirFunctionDeclaration implementation, int arity, string constraints, TypeNode[] parameters,
        TypeNode result, bool valueReturn, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
        => new(implementation, arity + "<" + constraints + ">(" + string.Join(",", parameters.Select(type =>
            ReferencedPhysicalTypeKey(type, refs, isValue))) + ")->" + ReferencedPhysicalTypeKey(result, refs, isValue)
            + (valueReturn ? "[value-return]" : ""));

    static BirFunctionDeclaration BuildSourceBridge(SourceDefinition owner, BirFunctionDeclaration implementation,
        TypeNode[] parameters, TypeNode result, TypeNode logicalResult, string name, bool valueReturn, TypeNode[] signature = null)
    {
        var source = implementation.Params.OrEmpty();
        var bridgeParameters = parameters.Select((type, index) => {
            var parameter = new BirParameter { Name = source[index].Name.OrDefault() ?? "p" + index,
                Type = BirGraphValue.CopyType(type) };
            // State-machine spills consume the value type, not the slot's function discriminator.
            if (signature?[index] is TypeNode.Mod { Req: false } discriminator && signature[index] != type)
                parameter.FunctionSignatureType = BirGraphValue.CopyType(discriminator.M);
            return parameter;
        }).ToArray();
        var ownerArguments = owner.OwnArguments;
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn(owner.Name, ownerArguments.Length == 0 ? null : ownerArguments),
            Virtual = true, ClrOwnerResolved = true, _exactExistentialBridgeOwnerCall = true,
            Recv = new BirThis(), Method = implementation.Name,
            Sig = source.Select(FunctionSignatureIdentity.SignatureType).ToArray(),
            DynRet = BirGraphValue.CopyType(implementation.Ret.OrDefault()), Ret = BirGraphValue.CopyType(implementation.Ret.OrDefault()),
            Args = bridgeParameters.Select(parameter => (IBirExpression)new BirLocal { Name = parameter.Name }).ToArray() };
        if (SourceSuspend(implementation)) call.SuspendCall = true;
        if (implementation.TypeParams.HasValue)
            call.TypeArgs = Enumerable.Range(0, implementation.TypeParams.Value.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
        var bridge = new BirFunctionDeclaration { Name = name, Static = false, Override = false, Virtual = true,
            Abstract = false, ObjectOverride = false, Vis = "private", Generated = true, Params = bridgeParameters,
            Ret = BirGraphValue.CopyType(result), Body = new IBirStatement[] { new BirReturn { Value = call } },
            Attrs = Array.Empty<BirAttribute>(), PhysicalSlotBridge = true, OverrideAdapterGroup = new object(),
            ClrInterfaceImpls = Array.Empty<BirMethodImpl>() };
        if (valueReturn) bridge.DotktValueReturn = true;
        if (owner.Kind == "interface") bridge.ClrInterfaceSlotBridge = true;
        if (SourceSuspend(implementation))
        {
            bridge.Mods = new BirModifiers { Suspend = true };
            bridge.SuspendRet = BirGraphValue.CopyType(result);
            bridge.SuspendResult = BirGraphValue.CopyType(logicalResult
                ?? throw new InvalidOperationException("suspend covariant bridge has no logical slot result"));
        }
        if (implementation.TypeParams.HasValue)
            bridge.TypeParams = implementation.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        return MaterializedExecutable.NormalizeSource(bridge);
    }

    static BirMethodImpl SourceDescriptor(TypeNode.Fqn owner, string member, int arity, TypeNode[] parameters,
        TypeNode result, BirTypeParameter[] typeParameters, bool valueReturn)
    {
        var descriptor = new BirMethodImpl { Owner = BirGraphValue.CopyType(owner), Member = member, Arity = arity,
            Params = parameters.Select(BirGraphValue.CopyType).ToArray(), Ret = BirGraphValue.CopyType(result) };
        if (valueReturn) descriptor.DotktValueReturn = true;
        if (typeParameters != null) descriptor.TypeParams = typeParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        return descriptor;
    }

    static IEnumerable<TypeNode.Fqn> ReachableSourceInterfaces(SourceDefinition owner,
        IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        var queue = new Queue<TypeNode.Fqn>(owner.Interfaces);
        var seen = new HashSet<TypeNode.Fqn>();
        while (queue.TryDequeue(out var application))
        {
            if (!seen.Add(application)) continue;
            yield return application;
            if (!definitions.TryGetValue(application.Name, out var declaration)) continue;
            var arguments = EffectiveArgs(application, declaration.Arity);
            if (arguments == null) continue;
            foreach (var parent in declaration.Interfaces) queue.Enqueue((TypeNode.Fqn)SubstOwnerTvs(parent, arguments));
        }
    }

    static bool SameSourceIdentity(BirFunctionDeclaration method, string physicalName, string property, string accessor)
        => property != null ? KotlinPropertyAccessors.TryIdentity(method, out var name, out var kind) && name == property && kind == accessor
            : method.Name.OrDefault() == physicalName && !KotlinPropertyAccessors.TryIdentity(method, out _, out _);
    static bool SourceOverrides(BirFunctionDeclaration method, string declarationId, string owner, string member, string kind)
        => method.Overrides.OrEmpty().Any(edge => edge.Owner.OrDefault() is TypeNode.Fqn named && named.Name == owner
            && edge.Member.OrDefault() == member && edge.Kind.OrDefault() == kind
            && (declarationId == null || edge.DeclarationId.OrDefault() == KotlinOverrideSlotBridge.SourceDeclarationId(declarationId)));
}
