using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DotKt.Toolchain;

static partial class ClrMemberResolution
{
    /// <summary>The exact external operands of the resolved interface MethodImpl tables.</summary>
    /// <remarks>
    /// Exact name/signature implementations bind under ordinary CLR rules. Each explicit obligation already names
    /// its body and declaration shape in clrInterfaceImpls. Resolve only those rows; ilemit consumes the resulting
    /// owner-scoped table without enumerating interfaces or searching the implementing type's overload set.
    /// </remarks>
    // Final physical declarations are indexed module-wide before any owner is stamped.
    // This inventory retains the actual mutable MethodDefs, not reconstructed source declarations.
    public static void ResolveInterfaceSlots(IReadOnlySet<string> bindingLocalTypes, IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, bool metadataBuild = false)
    {
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var definitions = new Dictionary<(string Name, int Arity), IBirTypeDeclaration>();
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
                if (type.Name.HasValue) definitions[(type.Name.Value, TypeParameterFrame.Count(type))] = type;
        foreach (var root in roots)
            root.Visit(node => {
                if (node is IBirTypeDeclaration type && type.Name.HasValue)
                    StampInterfaceSlots(bindingLocalTypes, type, definitions, refs, metadataBuild);
            });
    }

    static OptionalField<BirInterfaceSlotSet[]> InterfaceSlotSets(IBirTypeDeclaration type) => type switch {
        BirClassDefinition value => value.InterfaceSlotRefs,
        BirInterfaceDefinition value => value.InterfaceSlotRefs,
        BirEnumDefinition value => value.InterfaceSlotRefs,
        _ => throw new InvalidOperationException("Unaccounted physical interface-slot owner"),
    };
    static void SetInterfaceSlotSets(IBirTypeDeclaration type, BirInterfaceSlotSet[] sets)
    {
        switch (type) {
            case BirClassDefinition value: value.InterfaceSlotRefs = sets; break;
            case BirInterfaceDefinition value: value.InterfaceSlotRefs = sets; break;
            case BirEnumDefinition value: value.InterfaceSlotRefs = sets; break;
            default: throw new InvalidOperationException("Unaccounted physical interface-slot owner");
        }
    }

    sealed record InterfaceCandidate(MethodInfo Method, MemberRefNode Reference,
        TypeNode[] DeclaringArguments, TypeNode[] Parameters, TypeNode Return, Type Edge);

    static List<InterfaceCandidate> MetadataWinners(List<InterfaceCandidate> candidates)
    {
        var mostDerived = ClrDeclarationMatching.MostDerived(candidates.Select(candidate => candidate.Method)
            .DistinctBy(method => (method.Module, method.MetadataToken)).ToList());
        if (mostDerived.Count == 1)
        {
            var winner = mostDerived[0];
            candidates = candidates.Where(candidate => candidate.Method.Module == winner.Module
                && candidate.Method.MetadataToken == winner.MetadataToken).ToList();
        }
        return candidates.DistinctBy(candidate => (candidate.Method.Module, candidate.Method.MetadataToken, candidate.Reference.DeclaringType)).ToList();
    }

    static List<InterfaceCandidate> OrdinaryInterfaceWinners(List<InterfaceCandidate> candidates) =>
        candidates.Where(candidate => !candidates.Any(other => other.Edge != candidate.Edge
                && other.Edge.GetInterfaces().Contains(candidate.Edge)))
            .DistinctBy(candidate => (candidate.Method.Module, candidate.Method.MetadataToken, candidate.Reference.DeclaringType)).ToList();

    // A local declaration at another arity can hide the reference spelling,
    // but not the runtime interface that owns a resolved MethodImpl.
    static Type ExternalSlotOwner(TypeNode.Fqn face, ReferenceMetadataIndex refs) =>
        ResolveOwnerType(face, refs) ?? refs.PhysicalTypeNamed(face.Name, face.Args?.Length ?? 0);

    static void StampInterfaceSlots(IReadOnlySet<string> bindingLocalTypes, IBirTypeDeclaration type,
        IReadOnlyDictionary<(string Name, int Arity), IBirTypeDeclaration> definitions, ReferenceMetadataIndex refs, bool metadataBuild)
    {
        if (InterfaceSlotSets(type).IsPresent || !type.Methods.HasValue) return;
        var methods = type.Methods.Value;
        var descriptors = methods.SelectMany(method => method.ClrInterfaceImpls.OrEmpty())
            .Where(descriptor => descriptor.Owner.OrDefault() is TypeNode.Fqn)
            .Select(descriptor => (Owner: (TypeNode.Fqn)descriptor.Owner.Value, Descriptor: descriptor)).ToArray();
        if (descriptors.Length == 0) return;
        var slotSets = new Dictionary<string, BirInterfaceSlotSet>(StringComparer.Ordinal);
        var owners = new List<(TypeNode.Fqn Spec, Type External)>();
        foreach (var group in descriptors.GroupBy(entry => SupertypeGraph.TypeKey(entry.Owner), StringComparer.Ordinal))
        {
            var iface = group.First().Owner;
            // An application's canonical runtime template is not an emitted local
            // declaration when the reference set owns its actual CLR identity.
            if (definitions.TryGetValue((iface.Name, iface.Args?.Length ?? 0), out var local)
                && (!local.Generated.OrDefault() || !ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(iface.Name)
                    || refs.PhysicalTypeNamed(iface.Name, TypeParameterFrame.Count(local)) == null))
            {
                ResolveLocalInterfaceDescriptors(iface, local, group.Select(entry => entry.Descriptor));
                owners.Add((iface, null));
                continue;
            }
            var open = ExternalSlotOwner(iface, refs)
                ?? throw new InvalidOperationException($"bir2cir: interface MethodImpl owner '{iface.Name}' does not resolve in the reference set");
            var arguments = iface.Args ?? Array.Empty<TypeNode>();
            var declarationOwners = metadataBuild ? refs.MetadataInterfaceOwners(open, arguments)
                    .Select(edge => (edge.Definition, edge.Owner, Edge: (Type)null)).ToArray()
                : new[] { open }.Concat(open.GetInterfaces())
                    .Select(edge => (Definition: ClrDeclarationMatching.SafeDef(edge), Owner: (TypeNode.Fqn)null, Edge: edge)).ToArray();
            foreach (var entry in group)
            {
                var descriptor = entry.Descriptor;
                var (member, arity, parameters, result) = ReadInterfaceDescriptor(iface, descriptor, local: false);
                var named = declarationOwners.SelectMany(edge => edge.Definition.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == member && IsPublicOrProtected(method) && method.GetGenericArguments().Length == arity
                        && method.GetParameters().Length == parameters.Length)
                    .Select(method => {
                        var reference = metadataBuild ? ClrMemberReferences.MetadataDeclaration(refs, method, edge.Owner)
                            : ClrMemberReferences.MemberRefOf(refs, bindingLocalTypes, method, MemberRefNode.Kinds.Method, open, arguments, selectedEdge: edge.Edge);
                        var declaringArguments = (reference.DeclaringType as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();
                        return new InterfaceCandidate(method, reference, declaringArguments,
                            reference.ParameterTypes.Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, declaringArguments)).ToArray(),
                            SupertypeGraph.SubstOwnerTvs(reference.ReturnType, declaringArguments), edge.Edge);
                    })).ToList();
                var candidates = named.Where(candidate => candidate.Parameters.SequenceEqual(parameters, InterfaceSlotTypeComparer.Instance)
                    && SameInterfaceSlotType(candidate.Return, result)).ToList();
                if (descriptor.TypeParams.HasValue)
                {
                    var shape = KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(descriptor.TypeParams.Value, Array.Empty<TypeNode>());
                    candidates = candidates.Where(candidate => {
                        var declared = candidate.Method.GetGenericArguments()
                            .Select(gp => ReferenceMetadataReader.GenericParamDeclaration(gp, native: !refs.HasDotKtOwner(iface.Name))).ToArray();
                        return KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(declared, candidate.DeclaringArguments) == shape;
                    }).ToList();
                }
                var declarations = metadataBuild ? MetadataWinners(candidates) : OrdinaryInterfaceWinners(candidates);
                if (declarations.Count != 1)
                {
                    var body = methods.FirstOrDefault(method => method.ClrInterfaceImpls.OrEmpty().Any(value => ReferenceEquals(value, descriptor)));
                    var wanted = $"({string.Join(", ", parameters.Select(TypeNode.ToJson))}) -> {TypeNode.ToJson(result)}";
                    var available = named.Count == 0 ? "<none>" : string.Join("; ", named.Select(candidate =>
                        $"{candidate.Reference.DeclaringType}.({string.Join(", ", candidate.Parameters.Select(TypeNode.ToJson))}) -> {TypeNode.ToJson(candidate.Return)}"));
                    throw new InvalidOperationException($"bir2cir: interface MethodImpl '{iface.Name}.{member}`{arity}' resolves to "
                        + $"{declarations.Count} external declaration(s), expected exactly one; wanted {wanted}; named candidates: {available}; "
                        + $"body: {type.Name.OrDefault() ?? "?"}.{body?.Name.OrDefault() ?? "?"}");
                }
                var selected = declarations[0];
                if (selected.Reference.DeclaringType is not TypeNode.Fqn declaringOwner)
                    throw new InvalidOperationException($"bir2cir: interface MethodImpl '{iface.Name}.{member}`{arity}' resolved without a declaring type");
                descriptor.Owner = BirGraphValue.CopyType(declaringOwner);
                owners.Add((declaringOwner, selected.Method.DeclaringType));
                descriptor.Member = selected.Reference.Name;
                descriptor.Params = selected.Parameters.Select(BirGraphValue.CopyType).ToArray();
                descriptor.Ret = BirGraphValue.CopyType(selected.Return);
                var assembly = selected.Reference.Assembly;
                if (string.IsNullOrEmpty(assembly))
                    throw new InvalidOperationException($"bir2cir: external interface '{declaringOwner.Name}' has no assembly identity");
                var setKey = assembly + "::" + SupertypeGraph.TypeKey(declaringOwner);
                if (!slotSets.TryGetValue(setKey, out var set))
                {
                    set = new BirInterfaceSlotSet { Owner = BirGraphValue.CopyType(declaringOwner), Assembly = assembly,
                        Slots = Array.Empty<BirMemberReference>() };
                    slotSets.Add(setKey, set);
                }
                var slot = BirMemberReference.FromPhysical(selected.Reference);
                if (!set.Slots.Value.Any(existing => existing.StructurallyEquals(slot)))
                    set.Slots = set.Slots.Value.Append(slot).ToArray();
            }
        }
        // Normalize to exact declarations before deduplication: mixed Kotlin/CLR
        // spellings such as int/System.Int32 must not be guessed equivalent early.
        DeduplicateResolvedInterfaceDescriptors(methods);
        DeclareReimplementedInterfaces(type, owners, definitions, refs);
        if (slotSets.Count != 0) SetInterfaceSlotSets(type, slotSets.Values.ToArray());
    }

    // A MethodImpl identifies an exact constructed interface slot. The CLI requires its
    // declaring interface to be implemented here, not solely through the base class.
    static void DeclareReimplementedInterfaces(IBirTypeDeclaration type,
        List<(TypeNode.Fqn Spec, Type External)> owners,
        IReadOnlyDictionary<(string Name, int Arity), IBirTypeDeclaration> definitions, ReferenceMetadataIndex refs)
    {
        if (type.Base.OrDefault() is not TypeNode.Fqn) return;
        var direct = type.Interfaces.OrEmpty();
        // Keep the selected TypeDef across imported edges: a nested arity or the
        // defining assembly can distinguish two otherwise identical owner spellings.
        (Type, TypeNode) Key(Type external, TypeNode.Fqn spec)
        {
            if (external?.IsConstructedGenericType == true) external = external.GetGenericTypeDefinition();
            return (external, BirTypeLowering.CanonicalPhysicalSlotType(new TypeNode.Fqn(external?.FullName ?? spec.Name, spec.Args)));
        }
        var declared = new HashSet<(Type, TypeNode)>();
        void Include(TypeNode.Fqn face, HashSet<(Type, TypeNode)> result, Type external = null)
        {
            var args = face.Args ?? [];
            if (external == null && definitions.TryGetValue((face.Name, args.Length), out var local)
                && (!local.Generated.OrDefault() || !ManagedReferenceCatalog.IsCanonicalRuntimeSyntheticType(face.Name)
                    || refs.PhysicalTypeNamed(face.Name, args.Length) == null))
            {
                if (!result.Add(Key(null, face))) return;
                foreach (var parent in local.Interfaces.OrEmpty().OfType<TypeNode.Fqn>())
                    Include((TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(parent, args), result);
                return;
            }
            external ??= ExternalSlotOwner(face, refs);
            if (external == null || !result.Add(Key(external, face))) return;
            if (external.IsConstructedGenericType) external = external.GetGenericTypeDefinition();
            foreach (var (definition, spec) in refs.MetadataInterfaceOwners(external, args))
                result.Add(Key(definition, spec));
        }
        foreach (var face in direct.OfType<TypeNode.Fqn>()) Include(face, declared);
        // A required child already covers every ancestor it declares. Compute
        // coverage before adding rows so MethodDef order cannot affect the result.
        var pending = owners.DistinctBy(x => Key(x.External, x.Spec))
            .Where(x => !declared.Contains(Key(x.External, x.Spec))).ToArray();
        foreach (var (face, external) in pending)
        {
            var closure = new HashSet<(Type, TypeNode)>();
            Include(face, closure, external);
            closure.Remove(Key(external, face));
            declared.UnionWith(closure);
        }
        var added = new List<TypeNode>();
        foreach (var (face, external) in pending)
            if (!declared.Contains(Key(external, face))) added.Add(face);
        if (added.Count != 0) type.Interfaces = direct.Concat(added).ToArray();
    }

    static (string Member, int Arity, TypeNode[] Parameters, TypeNode Return) ReadInterfaceDescriptor(
        TypeNode.Fqn iface, BirMethodImpl descriptor, bool local)
    {
        var prefix = local ? "local " : "";
        if (!descriptor.Member.HasValue || !descriptor.Arity.HasValue || !descriptor.Params.HasValue || !descriptor.Ret.HasValue)
            throw new InvalidOperationException($"bir2cir: malformed {prefix}interface MethodImpl descriptor on '{iface.Name}'");
        if (descriptor.Params.Value.Any(parameter => parameter == null))
            throw new InvalidOperationException($"bir2cir: {prefix}interface MethodImpl '{iface.Name}.{descriptor.Member.Value}' has an unreadable parameter type");
        return (descriptor.Member.Value, descriptor.Arity.Value, descriptor.Params.Value, descriptor.Ret.Value);
    }

    static void ResolveLocalInterfaceDescriptors(TypeNode.Fqn iface, IBirTypeDeclaration definition, IEnumerable<BirMethodImpl> descriptors)
    {
        var arguments = SupertypeGraph.EffectiveArgs(iface, TypeParameterFrame.Count(definition));
        if (arguments == null)
            throw new InvalidOperationException($"bir2cir: local interface MethodImpl owner '{SupertypeGraph.TypeKey(iface)}' has invalid arity");
        foreach (var descriptor in descriptors)
        {
            var (member, arity, parameters, result) = ReadInterfaceDescriptor(iface, descriptor, local: true);
            // Physical property/accessor names have already been allocated. Match
            // that final MethodDef name, never a reconstructed Kotlin spelling.
            var candidates = definition.Methods.OrEmpty().Where(method => {
                if (method.Static.OrDefault() || method.PhysicalSlotBridge.OrDefault() || !method.Name.HasValue
                    || method.TypeParams.OrEmpty().Length != arity || !method.Params.HasValue
                    || method.Params.Value.Length != parameters.Length || !method.Ret.HasValue || method.Name.Value != member)
                    return false;
                var signature = method.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
                return signature.Length == parameters.Length && signature.All(parameter => parameter != null)
                    && signature.Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, arguments))
                        .SequenceEqual(parameters, InterfaceSlotTypeComparer.Instance)
                    && SameInterfaceSlotType(SupertypeGraph.SubstOwnerTvs(method.Ret.Value, arguments), result)
                    && (!descriptor.TypeParams.HasValue || KotlinOverrideSlotBridge.SameSourceMethodTypeParameterShape(
                        method.TypeParams.OrDefault(), descriptor.TypeParams.Value, arguments, Array.Empty<TypeNode>()));
            }).ToList();
            if (candidates.Count != 1)
            {
                var available = string.Join("; ", definition.Methods.OrEmpty().Select(method => {
                    KotlinPropertyAccessors.TryIdentity(method, out var property, out var accessor);
                    var signature = method.Params.HasValue
                        ? string.Join(", ", method.Params.Value.Select(parameter => TypeNode.ToJson(FunctionSignatureIdentity.SignatureType(parameter)))) : "?";
                    var resultType = method.Ret.HasValue ? TypeNode.ToJson(method.Ret.Value) : "?";
                    return $"{method.Name.OrDefault() ?? "?"} [property={property ?? "-"}:{accessor ?? "-"}] ({signature}) -> {resultType}";
                }));
                throw new InvalidOperationException($"bir2cir: local interface MethodImpl '{iface.Name}.{member}`{arity}' resolves to "
                    + $"{candidates.Count} declaration(s), expected exactly one; wanted: [{string.Join(", ", parameters.Select(TypeNode.ToJson))}]; candidates: {available}");
            }
            var selected = candidates[0];
            descriptor.Member = selected.Name;
            descriptor.Params = selected.Params.Value.Select(FunctionSignatureIdentity.SignatureType)
                .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, arguments)).ToArray();
            descriptor.Ret = SupertypeGraph.SubstOwnerTvs(selected.Ret.Value, arguments);
        }
    }

    static void DeduplicateResolvedInterfaceDescriptors(IEnumerable<BirFunctionDeclaration> methods)
    {
        foreach (var method in methods)
        {
            if (!method.ClrInterfaceImpls.HasValue) continue;
            var retained = new List<BirMethodImpl>();
            foreach (var descriptor in method.ClrInterfaceImpls.Value)
                if (!retained.Any(prior => prior.StructurallyEquals(descriptor))) retained.Add(descriptor);
            if (retained.Count != method.ClrInterfaceImpls.Value.Length) method.ClrInterfaceImpls = retained.ToArray();
        }
    }
}
