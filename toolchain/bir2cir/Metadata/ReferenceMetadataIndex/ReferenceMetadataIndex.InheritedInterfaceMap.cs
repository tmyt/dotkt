using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using DotKt.Bir;
using static ReferenceMetadataReader;

sealed partial class ReferenceMetadataIndex
{
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
        TypeNode[] Signature(MethodInfo method, TypeNode[] args)
        {
            var signature = MetadataMethodSignature(method);
            return signature.Parameters.Append(signature.Return).Select(p => Key(Close(p, args))).ToArray();
        }
        IEnumerable<MethodInfo> Named(Type type, BindingFlags flags) =>
            type.GetMember(member, MemberTypes.Method, flags).OfType<MethodInfo>().Where(m => m.GetGenericArguments().Length == arity);
        var slots = Named(iface, declared).Where(m => Signature(m, face.Args ?? []).SequenceEqual(expected)).Take(2).ToArray();
        if (slots.Length != 1) return false;
        var stack = new Stack<Type>();
        for (var t = root; t != null; t = t.BaseType) stack.Push(t);
        bool occupied = false, mapped = false, found = false;
        while (stack.TryPop(out var type))
        {
            var definition = Def(type);
            var args = Args(type).Select(p => Close(p, actual)).ToArray();
            var spec = new TypeNode.Fqn(definition.FullName, args.Length == 0 ? null : args);
            var candidates = Named(definition, declared).Where(m => Signature(m, args).SequenceEqual(expected)).Take(2).ToArray();
            if (candidates.Length > 1) return false;
            if (candidates is [var method])
            {
                if (!method.IsVirtual || occupied && (method.Attributes & MethodAttributes.NewSlot) != 0)
                    return false;
                occupied = true;
                found |= definition == selected && method.MetadataToken == token && Key(spec) == Key(body);
            }
            var reader = _universe.ModuleMetadata(definition.Module);
            var row = reader.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)MetadataTokens.Handle(definition.MetadataToken));
            foreach (var h in row.GetMethodImplementations())
            {
                var declaration = reader.GetMethodImplementation(h).MethodDeclaration;
                if (TryMethodImplMember(reader, declaration, out var parent, out var name) && name == member
                    && Key(Close(NativeSignatureReader.Type(reader, parent), args)) == Key(face)) return false;
            }
            // Bind in the *declaring class's open frame*, not just the user's
            // closed application: M(T) and M(int) can coincide at Base<int>.
            foreach (var edge in MetadataInterfaceOwners(definition, Args(definition)))
            {
                if (edge.Definition != iface || Key(Close(edge.Owner, args)) != Key(face)) continue;
                var openSlot = Signature(slots[0], edge.Owner.Args ?? []);
                var matches = Named(definition, BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.IsVirtual && Signature(m, Args(m.DeclaringType)).SequenceEqual(openSlot)).Take(2).ToArray();
                if (matches.Length != 1) return false;
                mapped = true;
            }
        }
        return found && mapped;
    }
}
