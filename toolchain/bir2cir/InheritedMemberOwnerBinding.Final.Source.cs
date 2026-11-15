using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedMemberOwnerBinding
{
    // This is a current physical lookup, not the captured source-selection
    // catalog. Duplicate type names are last-wins; declaration IDs must be unique.
    sealed class PhysicalTypeDef : TypeShape
    {
        public IBirTypeDeclaration Declaration;
        public BirFunctionDeclaration[] Methods => Declaration.Methods.OrDefault();
        public BirInheritedDefaultMethod[] Defaults => Declaration switch {
            BirClassDefinition value => value.InheritedDefaultMethods.OrDefault(),
            BirInterfaceDefinition value => value.InheritedDefaultMethods.OrDefault(),
            _ => null,
        };
    }

    readonly record struct PhysicalDeclaration(string Owner, BirFunctionDeclaration Method);

    // A call-specific view of existing facts. Writers only replace owned scalar
    // type facts; receiver, argument and registered descendant records never move.
    sealed record PhysicalUse(TypeNode.Fqn Owner, IBirExpression Receiver, bool Super,
        bool Resolved, bool Bound, string Method, int Arity, TypeNode[] Signature,
        int ParameterCount, string DeclarationId, string PropertyName, string PropertyAccessor,
        IReadOnlyList<BirOverride> Overrides, Action<TypeNode.Fqn> SetOwner,
        Action<TypeNode[]> SetSignature = null, Action<bool> SetVirtual = null,
        Action<string> SetMethod = null);

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var types = new Dictionary<string, PhysicalTypeDef>(StringComparer.Ordinal);
        foreach (var root in roots)
        foreach (var declaration in root.Types.OrEmpty())
        {
            var shape = SourceShape(declaration);
            if (shape?.Name == null) continue;
            types[shape.Name] = new PhysicalTypeDef { Name = shape.Name, Kind = shape.Kind,
                TypeParamCount = shape.TypeParamCount, Base = shape.Base, Interfaces = shape.Interfaces,
                Declaration = declaration };
        }
        var candidates = new Dictionary<string, List<PhysicalDeclaration>>(StringComparer.Ordinal);
        foreach (var type in types.Values)
        foreach (var method in type.Methods ?? Array.Empty<BirFunctionDeclaration>())
        {
            if (!method.DeclarationId.HasValue) continue;
            var id = method.DeclarationId.Value;
            if (!candidates.TryGetValue(id, out var rows)) candidates[id] = rows = new();
            rows.Add(new(type.Name, method));
        }
        var declarations = candidates.Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.Ordinal);
        foreach (var root in roots) Walk(root, null);

        void Walk(BirGraphValue node, TypeNode.Fqn enclosing)
        {
            if (node is null or IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration definition && SourceShape(definition)?.Name is string name
                && definition.Methods.HasValue && types.TryGetValue(name, out var type)
                && ReferenceEquals(type.Methods, definition.Methods.Value))
                enclosing = new(name, type.TypeParamCount == 0 ? null : Enumerable.Range(0, type.TypeParamCount)
                    .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
            if (PhysicalCall(node) is { } call)
            {
                BindPhysical(call, types, declarations, refs, enclosing);
                if (call.Owner != PhysicalCall(node)?.Owner)
                    ConstructedMemberReturnSubstitution.ApplyCall(node);
            }
            // The existing owner is preorder: enclosing declarations and a
            // receiver's original stamp are read before walking that receiver.
            node.VisitChildren(child => Walk(child, enclosing));
        }
    }

    static TypeNode[] PhysicalSignature(TypeNode[] types) => types?.All(type => type != null) == true ? types : null;
    static TypeNode[] CopyPhysicalSignature(IEnumerable<TypeNode> types) => types.Select(type => BirGraphValue.CopyType(type)).ToArray();

    static PhysicalUse PhysicalCall(BirGraphValue node)
    {
        switch (node)
        {
            case BirCallInstance call:
                KotlinPropertyAccessors.TryCallIdentity(call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(),
                    call.Method.OrDefault(), call.Prop.OrDefault(), out var property, out var accessor);
                return new(call.OwnerType.OrDefault() as TypeNode.Fqn, call.Recv.OrDefault(), call.Super.OrDefault(),
                    call.ClrOwnerResolved.OrDefault(), false, call.Method.OrDefault(), call.TypeArgs.OrEmpty().Length,
                    PhysicalSignature(call.Sig.OrDefault()), call.Args.HasValue ? call.Args.Value.Length : -1,
                    call.DeclarationId.OrDefault(), property, accessor, call.Overrides.OrEmpty(),
                    owner => call.OwnerType = BirGraphValue.CopyType(owner), sig => call.Sig = CopyPhysicalSignature(sig),
                    value => call.Virtual = value);
            case BirNewBoundDelegate call:
                return new(call.OwnerType.OrDefault() as TypeNode.Fqn, call.Recv.OrDefault(), false,
                    call.ClrOwnerResolved.OrDefault(), true, call.Method.OrDefault(), call.TypeArgs.OrEmpty().Length,
                    PhysicalSignature(call.Sig.OrDefault()), -1, call.DeclarationId.OrDefault(), null, null,
                    Array.Empty<BirOverride>(), owner => { call.OwnerType = BirGraphValue.CopyType(owner); call.CalleeOwner = BirGraphValue.CopyType(owner); },
                    sig => call.Sig = CopyPhysicalSignature(sig), value => call.Virtual = value, value => call.Method = value);
            case BirNativeInstance call:
                return Clr(call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault(), owner => call.Type = BirGraphValue.CopyType(owner));
            case BirBoundClrDelegate call:
                return Clr(call.ClrType.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault(), owner => call.ClrType = BirGraphValue.CopyType(owner));
            case BirPropertyGet call:
                return Clr(call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault(), owner => call.Type = BirGraphValue.CopyType(owner));
            case BirPropertySet call:
                return Clr(call.Type.OrDefault(), call.Recv.OrDefault(), call.Super.OrDefault(), call.ClrOwnerResolved.OrDefault(), owner => call.Type = BirGraphValue.CopyType(owner));
            case BirEventAdd call:
                return Clr(call.Type.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault(), owner => call.Type = BirGraphValue.CopyType(owner));
            case BirEventRemove call:
                return Clr(call.Type.OrDefault(), call.Recv.OrDefault(), false, call.ClrOwnerResolved.OrDefault(), owner => call.Type = BirGraphValue.CopyType(owner));
            default: return null;
        }
        static PhysicalUse Clr(TypeNode owner, IBirExpression receiver, bool super, bool resolved, Action<TypeNode.Fqn> setOwner)
            => new(owner as TypeNode.Fqn, receiver, super, resolved, false, null, 0, null, -1, null, null, null,
                Array.Empty<BirOverride>(), setOwner);
    }

    static TypeNode.Fqn PhysicalReceiverStamp(IBirExpression receiver)
    {
        TypeNode stamp = null;
        if (receiver is BirGraphValue node)
            node.VisitOwnTypeFields((field, type) => { if (field == "sty") stamp = type; });
        return stamp as TypeNode.Fqn;
    }

    static void BindPhysical(PhysicalUse call, Dictionary<string, PhysicalTypeDef> types,
        IReadOnlyDictionary<string, PhysicalDeclaration> declarations, ReferenceMetadataIndex refs, TypeNode.Fqn enclosing)
    {
        if (call.Resolved || call.Owner is not { } owner) return;
        if (ProjectUseOwner(owner, PhysicalReceiverStamp(call.Receiver), call.Receiver is BirThis, call.Super,
                enclosing, types, refs, sourceFrames: false) is { } projected)
        { owner = projected; call.SetOwner(owner); }
        // CLR-shaped nodes already have a member kind. Only their construction
        // belongs here; the later exact CLR resolver still selects descriptors.
        if (call.SetSignature == null || call.Method is not string method) return;
        if (!types.ContainsKey(owner.Name) && !refs.TryReferenceTypeShape(owner, out _, out _, out _, out _)) return;
        var sig = call.Signature;
        if (call.DeclarationId is string id)
        {
            var local = declarations.TryGetValue(id, out var selected);
            var selectedOwner = local ? selected.Owner : null;
            if (local || refs.TryDeclarationIdentity(id, out _, out selectedOwner, out _, out _))
            {
                var owners = ConstructedOwners(owner, selectedOwner, types, refs);
                if (owners.Count == 1)
                {
                    owner = owners[0]; call.SetOwner(owner);
                    if (local && selected.Method.Params.HasValue)
                    {
                        sig = selected.Method.Params.Value.Select(parameter => parameter.Type.OrDefault()
                            ?? throw new InvalidOperationException($"Local declaration '{id}' has an untyped parameter")).ToArray();
                        call.SetSignature(sig);
                    }
                }
            }
        }
        if (!call.Super && call.ParameterCount >= 0
            && (DeclaresPhysicalVirtual(types.GetValueOrDefault(owner.Name), call, sig)
                || !types.ContainsKey(owner.Name) && (call.PropertyAccessor != null
                    ? refs.DeclaresVirtualInstancePropertyAccessor(owner.Name, call.PropertyName, call.PropertyAccessor,
                        call.Arity, sig, call.ParameterCount, owner.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresVirtualInstanceMember(owner.Name, method, call.Arity, sig, call.ParameterCount))))
            call.SetVirtual(true);
        if (sig == null) return;
        if (PhysicalDeclarationSignature(types.GetValueOrDefault(owner.Name), call, sig, owner.Args) is { } own)
        { call.SetSignature(own); return; }
        // Class-super is a nonvirtual implementation lookup along classes ONLY.
        // Interface obligations must not intercept it; interface-super is exact.
        if (call.Super && !IsInterface(owner, types, refs))
        {
            call.SetVirtual(false);
            foreach (var baseOwner in BaseClassChain(owner, types, refs))
            {
                var local = PhysicalDeclarationSignature(types.GetValueOrDefault(baseOwner.Name), call, sig, baseOwner.Args);
                if (local == null && (types.ContainsKey(baseOwner.Name) || !ReferencedExact(baseOwner))) continue;
                call.SetOwner(baseOwner); if (local != null) call.SetSignature(local); return;
            }
            return;
        }
        if (call.Super) return;
        if (call.Bound && ExactPhysicalDefault(types.GetValueOrDefault(owner.Name), call, sig, owner.Args)?.Implementation.OrDefault()
                is { } implementation && implementation.Owner.OrDefault() is TypeNode.Fqn implementationOwner
            && implementation.Member.OrDefault() is string implementationMember)
        {
            var owners = ConstructedOwners(owner, implementationOwner.Name, types, refs);
            if (owners.Count != 1) return;
            call.SetOwner(owners[0]); call.SetMethod(implementationMember); call.SetVirtual(true); return;
        }
        // A local runtime declaration set is authoritative over its reference
        // twin's fake/abstract rows. Own concrete AND abstract slots stop lookup.
        if (!types.ContainsKey(owner.Name) && ReferencedExact(owner)) return;
        var overrideOwners = call.Overrides.Where(ov =>
            (call.PropertyAccessor == null ? ov.Kind.OrDefault() is null or "method"
                : ov.Kind.OrDefault() == (call.PropertyAccessor == "get" ? "getter" : "setter"))
            && ov.Member.OrDefault() == (call.PropertyName ?? method))
            .Select(ov => (ov.Owner.OrDefault() as TypeNode.Fqn)?.Name).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var matches = ReachableTypes(owner, types, refs).Where(row => !row.Type.Equals(owner))
            .Where(row => overrideOwners.Count == 0 || overrideOwners.Contains(row.Type.Name))
            .Where(row => PhysicalDeclarationSignature(types.GetValueOrDefault(row.Type.Name), call, sig, row.Type.Args) != null
                || !types.ContainsKey(row.Type.Name) && ReferencedExact(row.Type)).ToArray();
        if (matches.Length == 0) return;
        // Override facts are a closure: only collisions at the nearest depth
        // are ambiguous, not the same declaration reached through ancestors.
        var depth = matches.Min(row => row.Depth);
        var nearest = matches.Where(row => row.Depth == depth).Select(row => row.Type).Distinct().ToArray();
        if (nearest.Length != 1) return;
        call.SetOwner(nearest[0]);
        if (PhysicalDeclarationSignature(types.GetValueOrDefault(nearest[0].Name), call, sig, nearest[0].Args) is { } signature)
            call.SetSignature(signature);
        if (IsInterface(nearest[0], types, refs)) call.SetVirtual(true);

        bool ReferencedExact(TypeNode.Fqn target) => call.PropertyAccessor != null
            ? refs.DeclaresExactInstancePropertyAccessor(target.Name, call.PropertyName, call.PropertyAccessor,
                call.Arity, sig, target.Args ?? Array.Empty<TypeNode>())
            : refs.DeclaresExactInstanceMember(target.Name, method, call.Arity, sig, target.Args ?? Array.Empty<TypeNode>());
    }

    static bool PhysicalMethodIdentity(BirFunctionDeclaration method, PhysicalUse call) => call.PropertyAccessor != null
        ? KotlinPropertyAccessors.TryIdentity(method, out var property, out var accessor)
            && property == call.PropertyName && accessor == call.PropertyAccessor
        : method.Name.OrDefault() == call.Method && !KotlinPropertyAccessors.TryIdentity(method, out _, out _);

    static TypeNode[] PhysicalDeclarationSignature(PhysicalTypeDef def, PhysicalUse call, TypeNode[] sig, TypeNode[] ownerArgs)
    {
        if (def?.Methods == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;
        var matches = def.Methods.Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
            && PhysicalMethodIdentity(method, call) && !method.FakeOverride.OrDefault()
            && method.TypeParams.OrEmpty().Length == call.Arity && method.Params.HasValue
            && method.Params.Value.Length == sig.Length
            && method.Params.Value.Select((parameter, index) => PhysicalParameterMatches(parameter.Type.OrDefault(), sig[index], ownerArgs)).All(match => match)).ToArray();
        return matches.Length == 1 ? matches[0].Params.Value.Select(parameter => parameter.Type.OrDefault()).ToArray() : null;
    }

    static bool PhysicalParameterMatches(TypeNode declared, TypeNode use, TypeNode[] ownerArgs)
    {
        if (declared == null) return false;
        var substituted = SubstOwnerTvs(declared, ownerArgs);
        // These are descriptor equalities, not assignability. Nested object
        // erasure is allowed; a bare object must not select an arbitrary overload.
        return declared == use || substituted == use
            || IsNestedObjectErasureOf(declared, use) || IsNestedObjectErasureOf(substituted, use);
    }

    static BirInheritedDefaultMethod ExactPhysicalDefault(PhysicalTypeDef def, PhysicalUse call, TypeNode[] sig, TypeNode[] ownerArgs)
    {
        if (def?.Defaults == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;
        var matches = def.Defaults.Where(fact => fact.Member.OrDefault() == call.Method && fact.Params.HasValue
            && fact.Params.Value.Length == sig.Length && fact.Implementation.HasValue
            && fact.Implementation.Value.TypeParams.OrEmpty().Length == call.Arity
            && fact.Params.Value.Select((parameter, index) => PhysicalParameterMatches(parameter, sig[index], ownerArgs)).All(match => match)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    static bool DeclaresPhysicalVirtual(PhysicalTypeDef def, PhysicalUse call, TypeNode[] sig)
    {
        if (def?.Methods == null) return false;
        var matches = def.Methods.Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
            && PhysicalMethodIdentity(method, call) && method.TypeParams.OrEmpty().Length == call.Arity
            && method.Params.HasValue && method.Params.Value.Length == call.ParameterCount)
            .Where(method => sig == null || sig.Length == call.ParameterCount
                && method.Params.Value.Select((parameter, index) => parameter.Type.OrDefault() == sig[index]).All(match => match)).ToArray();
        return matches.Length == 1 && matches[0].Virtual.OrDefault();
    }
}
