using System.Reflection;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    sealed record ReferencedCallInput(string Kind, TypeNode Owner, string Name, TypeNode[] Signature,
        int MethodArity, string Identity, bool AlreadyBound, TypeNode[] SourceSignature,
        string PropertyName, string PropertyKind, string Prop, bool Super, string NativeMethodKey = null);

    sealed record ReferencedCallPlan(bool ClearIdentity = false, bool PropertySelected = false,
        string PropertyName = null, string PropertyKind = null, string PhysicalName = null, bool SetVirtual = false,
        TypeNode[] Signature = null, MemberRefNode Reference = null, MethodInfo Declaration = null,
        TypeNode[] DelegateOwnerArguments = null, bool IdentityBound = false, bool StampReturn = false);

    // This is the same final selector for the owning stage and the genuine
    // later physical DOM consumers. Readers/writers own transport, not matching.
    static ReferencedCallPlan PlanReferencedCall(ReferencedCallInput call, string context,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes, IReadOnlySet<string> externalCanonicalTypes,
        IReadOnlySet<string> localDeclarationIds)
    {
        if (call.AlreadyBound || call.Owner is not TypeNode.Fqn owner || call.Name == null || call.Signature == null)
            return new();
        // Local current declarations outrank a shipped twin. A referenced ID
        // temporarily carried by an inline/local wrapper must not cross onto
        // the local declaration-allocation axis.
        if (localTypes.Contains(owner.Name) && !externalCanonicalTypes.Contains(owner.Name))
            return new(ClearIdentity: call.Identity != null && !localDeclarationIds.Contains(call.Identity));
        if (call.Identity != null && localDeclarationIds.Contains(call.Identity)) return new();
        var signature = call.SourceSignature ?? call.Signature;
        if (signature.Any(type => type == null)) return new();
        KotlinPropertyAccessors.TryCallIdentity(call.PropertyName, call.PropertyKind, call.Name, call.Prop,
            out var propertyName, out var propertyKind);
        var name = call.Name;
        var property = false;
        var setVirtual = false;
        if (propertyKind is "get" or "set")
        {
            if (!refs.TryKotlinPropertyAccessor(owner.Name, propertyName, propertyKind, signature.Length, call.MethodArity,
                signature, owner.Args ?? Array.Empty<TypeNode>(), out var accessor, out var accessorVirtual)) return new();
            name = accessor;
            property = true;
            // A virtual declaration does not override Kotlin's selected super dispatch.
            setVirtual = accessorVirtual && !call.Super;
        }
        var isStatic = call.Kind is "callStatic" or "newDelegate";
        if (call.NativeMethodKey != null)
        {
            var open = ResolveOwnerType(owner, refs);
            var selected = ClrDeclarationMatching.MethodForNativeKey(refs, open, name, call.MethodArity, !isStatic, call.NativeMethodKey);
            var reference = ClrMemberReferences.MemberRefOf(refs, localTypes, selected, MemberRefNode.Kinds.Method, open, owner.Args);
            return new(Signature: reference.ParameterTypes, Reference: reference, Declaration: selected,
                DelegateOwnerArguments: (reference.DeclaringType as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>());
        }
        if (!property && call.Identity != null)
        {
            var callOwner = ResolveOwnerType(owner, refs)
                ?? throw new InvalidOperationException($"bir2cir: selected call owner '{owner.Name}' does not resolve");
            if (!refs.TryDeclarationIdentityMethod(localTypes, call.Identity, call.MethodArity, isStatic, signature,
                callOwner, owner.Args, out var selectedSignature, out var selected, out _, out var failure))
                throw new InvalidOperationException($"bir2cir: {context} [{call.Kind} {owner.Name}.{name}]: " +
                    $"frontend declaration identity '{call.Identity}' {failure}");
            // Identity selected the MethodDef, not the receiver's instantiation
            // of its declaring type. Project through that actual call owner.
            var reference = ClrMemberReferences.MemberRefOf(refs, localTypes, selected, MemberRefNode.Kinds.Method, callOwner, owner.Args);
            return new(ClearIdentity: true, Signature: selectedSignature, Reference: reference, Declaration: selected,
                DelegateOwnerArguments: (reference.DeclaringType as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>(),
                IdentityBound: true, StampReturn: call.Kind == "constrainedCall");
        }
        // Property association also owns compiler-added witness parameters;
        // do not route that source-property vector through ID selection again.
        var plan = new ReferencedCallPlan(ClearIdentity: property, PropertySelected: property,
            PropertyName: propertyName, PropertyKind: propertyKind, PhysicalName: name, SetVirtual: setVirtual);
        if (!refs.TryResolveStaticMemberSignature(owner.Name, name, call.MethodArity, isStatic, signature,
            owner.Args ?? Array.Empty<TypeNode>(), out var physicalSignature, out var declaration, out var declaringOwner))
            return plan;
        return plan with { Signature = physicalSignature,
            Reference = ClrMemberReferences.MemberRefOf(refs, localTypes, declaration, MemberRefNode.Kinds.Method, declaringOwner, owner.Args),
            Declaration = declaration, DelegateOwnerArguments = owner.Args ?? Array.Empty<TypeNode>() };
    }
}
