using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using DotKt.Bir;
using static ReferenceMetadataReader;

sealed partial class ReferenceMetadataIndex
{
    // The interface closure is independent of the member under consideration.
    readonly Dictionary<Type, (ILookup<Type, TypeNode.Fqn> Edges, HashSet<Type> Ambiguous)> _nativeInterfaceEdges = new();

    // A projected super.iterator() must call the named base's implementation, not
    // redispatch the interface on the derived receiver. Follow native mappings,
    // including an explicit MethodImpl whose body need not be named GetEnumerator.
    internal (MethodInfo Method, TypeNode.Fqn Owner) RawEnumeratorBaseTarget(TypeNode.Fqn owner)
    {
        var iface = _universe.CoreType("System.Collections.IEnumerable");
        var cursor = _universe.CoreType("System.Collections.IEnumerator");
        var start = ClrMemberResolution.ResolveOwnerType(owner, this);
        var chain = new Stack<(Type Def, TypeNode.Fqn Spec)>();
        for (var t = start; t != null; t = t.BaseType)
        {
            var decl = t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t;
            var spec = t == start ? owner : (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(NativeDeclarationType(t), owner.Args ?? []);
            chain.Push((decl, spec));
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        MethodInfo ExplicitBody(Type type)
        {
            var body = DeclaredMethodImpls(type).SingleOrDefault(row => row.Member == "GetEnumerator" &&
                row.NominalOwner.Name == iface.FullName && (row.NominalOwner.Scope == iface.Assembly.FullName ||
                    _universe.SameDefinition(iface.FullName, row.NominalOwner.Scope, iface.Assembly.FullName)) &&
                row.Signature is { GenericParameterCount: 0, ParameterTypes.Length: 0 });
            return body == null ? null : type.GetMethods(flags).Single(m => m.MetadataToken == body.BodyMethodDef);
        }
        (MethodInfo Method, TypeNode.Fqn Owner) selected = default, visible = default;
        var defaults = new Dictionary<(Type Definition, TypeNode.Fqn Owner), MethodInfo>();
        foreach (var (type, spec) in chain)
        {
            var methods = type.GetMethods(flags);
            var found = methods.SingleOrDefault(m => m.IsPublic && m.IsVirtual && m.Name == "GetEnumerator" && !m.IsGenericMethod
                && m.GetParameters().Length == 0 && m.ReturnType == cursor);
            var inheritedVirtual = visible.Method;
            if (found != null) visible = (found, spec);
            var edges = MetadataInterfaceOwners(type, spec.Args ?? []).Where(edge => edge.Definition.IsInterface).ToArray();
            foreach (var edge in edges)
                if (ExplicitBody(edge.Definition) is { } defaultBody) defaults[edge] = defaultBody;
            if (ExplicitBody(type) is { } body)
                selected = (body, spec);
            else if (found != null && (_metadataInterfaces[type].Any(edge => edge.Definition == iface)
                || (found.Attributes & MethodAttributes.NewSlot) == 0 && selected.Method is { IsFinal: false }
                    && inheritedVirtual == selected.Method))
                selected = (found, spec);
            else if (selected.Method == null && edges.Any(edge => edge.Definition == iface))
                selected = visible;
        }
        if (selected.Method == null)
        {
            var mostSpecific = defaults.Where(d => !defaults.Keys.Any(k => k != d.Key &&
                MetadataInterfaceOwners(k.Definition, k.Owner.Args ?? []).Contains(d.Key))).ToArray();
            if (mostSpecific is [var only]) selected = (only.Value, only.Key.Owner);
        }
        if (selected.Method is { IsAbstract: false } target &&
            (target.IsPublic || !target.DeclaringType.IsInterface && (target.IsFamily || target.IsFamilyOrAssembly)))
            return selected;
        throw new InvalidOperationException($"super.iterator() on '{owner.Name}' has no accessible concrete base implementation of IEnumerable.GetEnumerator()");
    }

    internal bool DeclaresExplicitInterfaceSlot(TypeNode.Fqn owner, TypeNode.Fqn face, string member,
        int arity, TypeNode[] parameters, TypeNode result)
    {
        if (ClrMemberResolution.ResolveOwnerType(owner, this) is not { IsInterface: false } type
            || ClrMemberResolution.ResolveOwnerType(face, this) is not { IsInterface: true } target) return false;
        TypeNode Key(TypeNode t) => ClrMemberResolution.MethodImplComparisonType(t, this);
        var actual = owner.Args ?? [];
        foreach (var row in DeclaredMethodImpls(type))
        {
            if (row.Member != member) continue;
            var (scope, name) = row.NominalOwner;
            if (name != target.FullName || scope != target.Assembly.FullName && !_universe.SameDefinition(name, scope, target.Assembly.FullName)
                || row.Owner is not { } openOwner) continue;
            var declared = (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(openOwner, actual);
            if (Key(declared) != Key(face)) continue;
            var signature = row.Signature;
            if (!signature.Header.IsInstance || signature.GenericParameterCount != arity
                || signature.ParameterTypes.Length != parameters.Length) continue;
            TypeNode Close(TypeNode t) => Key(SupertypeGraph.SubstOwnerTvs(t, declared.Args ?? []));
            if (Close(signature.ReturnType) == Key(result)
                && signature.ParameterTypes.Select(Close).SequenceEqual(parameters.Select(Key))) return true;
        }
        return false;
    }

    // A new derived interface does not replace the base's existing mapping of
    // its parent. Elide a forwarder only if its selected MethodDef is in the
    // same virtual slot as the native implicit implementation. On ambiguity
    // keep the already selected forwarding body.
    internal bool InheritsNativeInterfaceBody(TypeNode.Fqn baseType, TypeNode.Fqn body, int token,
        TypeNode.Fqn face, string member, int arity, TypeNode[] parameters, TypeNode result)
    {
        if (HasDotKtOwner(baseType.Name)
            || ClrMemberResolution.ResolveOwnerType(baseType, this) is not { IsInterface: false } root
            || ClrMemberResolution.ResolveOwnerType(body, this) is not {} selected
            || ClrMemberResolution.ResolveOwnerType(face, this) is not { IsInterface: true } iface) return false;
        TypeNode Key(TypeNode t) => ClrMemberResolution.MethodImplComparisonType(t, this);
        TypeNode Close(TypeNode t, TypeNode[] args) => SupertypeGraph.SubstOwnerTvs(t, args);
        TypeNode[] Args(Type t) => t.GetGenericArguments().Select(NativeDeclarationType).ToArray();
        Type Def(Type t) => t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t;
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var actual = baseType.Args ?? [];
        var expected = parameters.Append(result).Select(Key).ToArray();
        // The descriptor represents names without assembly scopes. Prove that
        // the actual signature leaves resolve to the same TypeDefs before using it.
        bool Scoped(Type t) => t.IsGenericParameter || (!t.IsFunctionPointer && (t.HasElementType
            ? (!t.IsArray || t.IsSZArray) && Scoped(t.GetElementType())
            : !IsLocalEmittedType(Def(t).FullName) && _universe.ResolveType(Def(t).FullName, 0, throwOnAmbiguity: false) == Def(t)
                && (!t.IsConstructedGenericType || t.GetGenericArguments().All(Scoped))));
        bool Plain(TypeNode t) => t switch {
            TypeNode.Fqn f => f.Args == null || f.Args.All(Plain), TypeNode.Tv => true,
            TypeNode.Array a => Plain(a.Elem), TypeNode.ByRef r => Plain(r.Of), _ => false,
        };
        bool Fits(MethodInfo method, TypeNode[] args, TypeNode[] target)
        {
            if (!Scoped(method.ReturnType) || !method.GetParameters().All(p => Scoped(p.ParameterType))) return false;
            var signature = MetadataMethodSignature(method);
            var types = signature.Parameters.Append(signature.Return);
            return types.All(Plain) && types.Select(p => Key(Close(p, args))).SequenceEqual(target);
        }
        IEnumerable<MethodInfo> Named(Type type, BindingFlags flags) =>
            type.GetMember(member, MemberTypes.Method, flags).OfType<MethodInfo>()
                .Where(m => m.GetGenericArguments().Length == arity && m.GetParameters().Length == parameters.Length);
        var slots = Named(iface, declared).Take(2).ToArray();
        if (slots.Length != 1 || !Fits(slots[0], face.Args ?? [], expected)) return false;
        var stack = new Stack<Type>();
        for (var t = root; t != null; t = t.BaseType) stack.Push(t);
        bool occupied = false, mapped = false, found = false;
        while (stack.TryPop(out var type))
        {
            var definition = Def(type);
            if (!Scoped(type)) return false;
            var args = Args(type).Select(p => Close(p, actual)).ToArray();
            var spec = new TypeNode.Fqn(definition.FullName, args.Length == 0 ? null : args);
            var candidates = Named(definition, declared | BindingFlags.NonPublic).Take(2).ToArray();
            if (candidates.Length > 1) return false;
            if (candidates is [var method])
            {
                if (!method.IsPublic || !method.IsVirtual || occupied && (method.Attributes & MethodAttributes.NewSlot) != 0
                    || !Fits(method, args, expected))
                    return false;
                occupied = true;
                found |= definition == selected && method.MetadataToken == token && Key(spec) == Key(body);
            }
            var reader = _universe.ModuleMetadata(definition.Module);
            var row = reader.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)MetadataTokens.Handle(definition.MetadataToken));
            foreach (var h in row.GetMethodImplementations())
            {
                var impl = reader.GetMethodImplementation(h);
                if (TryMethodImplMember(reader, impl.MethodDeclaration, out _, out var name) && name == member
                    || TryMethodImplMember(reader, impl.MethodBody, out _, out var bodyName) && bodyName == member) return false;
            }
            // Bind in the *declaring class's open frame*, not just the user's
            // closed application: M(T) and M(int) can coincide at Base<int>.
            if (!_nativeInterfaceEdges.TryGetValue(definition, out var interfaces))
                _nativeInterfaceEdges.Add(definition, interfaces = (MetadataInterfaceOwners(definition, Args(definition))
                    .ToLookup(edge => edge.Definition, edge => edge.Owner), definition.GetInterfaces().Where(t => !Scoped(t)).Select(Def).ToHashSet()));
            if (interfaces.Ambiguous.Contains(iface)) return false;
            foreach (var edge in interfaces.Edges[iface])
            {
                if (Key(Close(edge, args)) != Key(face)) continue;
                var slot = MetadataMethodSignature(slots[0]);
                var openSlot = slot.Parameters.Append(slot.Return).Select(t => Key(Close(t, edge.Args ?? []))).ToArray();
                var matches = Named(definition, BindingFlags.Instance | BindingFlags.Public).Take(2).ToArray();
                if (matches.Length != 1 || !matches[0].IsVirtual || !Scoped(matches[0].DeclaringType)
                    || !Fits(matches[0], Args(matches[0].DeclaringType), openSlot)) return false;
                mapped = true;
            }
        }
        return found && mapped;
    }
}
