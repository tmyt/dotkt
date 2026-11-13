using System.Reflection;
using DotKt.Bir;

static partial class NetInteropBinding
{
    enum NativeCallKind { Restricted, Event, Generic, EnumName, EnumOrdinal, EnumCompare, PropertyGet, PropertySet, Operator, Method }
    sealed record CallSelection(TypeNode Owner, Type NativeType, string Method, bool Static, NativeCallKind Kind, bool ClearProperty = false);
    sealed record FieldSelection(TypeNode Owner, string Name, bool Static, bool Restricted);
    sealed record Signatures(TypeNode[] Declaration, TypeNode[] Declared, TypeNode[] Generic, TypeNode[] Operator);

    static Signatures SelectSignatures(TypeNode[] arguments, TypeNode[] shapes, TypeNode[] signature)
        => new(arguments ?? shapes ?? signature, signature ?? shapes ?? arguments ?? Array.Empty<TypeNode>(),
            shapes ?? signature ?? Array.Empty<TypeNode>(), arguments ?? signature ?? Array.Empty<TypeNode>());

    static TypeNode[] NormalizeSignature(TypeNode[] signature) => signature?.Select(NormSigTv).ToArray() ?? Array.Empty<TypeNode>();

    // These decisions consume only declaration/metadata facts. Graph mutation
    // stays in the owning adapter; late users share the reflection helpers.
    static CallSelection SelectCall(ReferenceMetadataIndex refs, TypeNode ownerType, string method, bool staticCall,
        bool companionCall, string property, string visibility, int argumentCount, bool generic,
        IReadOnlyList<TypeNode> declarationArgs, IReadOnlyList<TypeNode> comparableSignature)
    {
        var owner = UnwrapFqn(ownerType);
        if (owner == null) return null;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        var physicalOwner = ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name);
        var reflectedOwner = refs.TryCompanionMetadataCarrier(bare, out var metadataCarrier) ? metadataCarrier : physicalOwner;
        bool? companionStatic = companionCall && refs.TryCompanionIsStatic(bare, out var resolvedStatic) ? resolvedStatic : null;
        var native = refs.ResolveNetType(physicalOwner, owner.Args?.Length ?? 0);
        if (native == null && method == "invoke" && refs.IsClrDelegate(owner))
            native = ClrMemberResolution.ResolveOwnerType(owner, refs);
        Type emitted = null;
        if (native == null && companionStatic != null)
            emitted = refs.ResolveCompanionMetadataCarrier(bare, owner.Args?.Length ?? 0);
        else if (native == null && refs.HasDotKtOwner(bare))
            emitted = refs.ResolveRefType(reflectedOwner, owner.Args?.Length ?? 0);
        if (companionCall && companionStatic == null && method != null && refs.HasDotKtOwner(bare)
            && refs.TryKotlinRichEnumStaticApi(bare, method, declarationArgs.Count, out var enumApi))
        {
            companionStatic = true;
            method = enumApi;
        }
        if (companionCall && companionStatic == null)
            throw new InvalidOperationException($"DotKt companion call owner '{bare}' has no trusted companion carrier");
        if (companionStatic == false) return null;
        if (companionStatic != null)
        {
            native = emitted ?? native ?? refs.ResolveRefType(reflectedOwner, owner.Args?.Length ?? 0)
                ?? throw new InvalidOperationException($"companion call owner '{bare}' is absent from the selected references");
            if (metadataCarrier != null && ownerType is TypeNode.Fqn direct)
                ownerType = new TypeNode.Fqn(metadataCarrier, direct.Args);
        }
        if (native == null && emitted?.IsEnum == true) native = emitted;
        if (native == null && !staticCall && method == "compareTo" && emitted is Type comparable
            && DeclaresPublicMethodNamed(comparable, "CompareTo") && IsComparableSelfCall(comparable, owner, comparableSignature))
            native = comparable;
        if (native == null) return null;
        if (!staticCall && method == "invoke" && refs.IsClrDelegate(owner)) method = "Invoke";
        var comparableCall = !staticCall && method == "compareTo" && IsComparableSelfCall(native, owner, comparableSignature);
        var isStatic = companionStatic ?? (staticCall || native.IsAbstract && native.IsSealed
            || property is "get" or "set" && MemberIsStaticPropertyOrField(native, method));
        if (visibility is "private" or "protected" or "internal")
        {
            var clearProperty = false;
            if (property is "index-get" or "index-set" && DefaultIndexerAccessor(native, property == "index-set") is { } accessor)
            { method = accessor; clearProperty = true; }
            return new(isStatic ? CloseStaticOwner(ownerType, native) : ownerType, native, method, isStatic, NativeCallKind.Restricted, clearProperty);
        }
        if (property == "get" && method != null && refs.TryClrEventIsStatic(physicalOwner, method, out var eventStatic))
            return new(eventStatic ? CloseStaticOwner(ownerType, native) : ownerType, native, method, eventStatic, NativeCallKind.Event);
        if (isStatic) ownerType = CloseStaticOwner(ownerType, native);
        if (generic) return new(ownerType, native, method, isStatic, NativeCallKind.Generic);
        if (!isStatic && IsNetEnum(native) && property == "get" && method is "name" or "ordinal")
            return new(ownerType, native, method, false, method == "name" ? NativeCallKind.EnumName : NativeCallKind.EnumOrdinal);
        if (!isStatic && IsNetEnum(native) && method == "compareTo" && argumentCount == 1)
            return new(ownerType, native, method, false, NativeCallKind.EnumCompare);
        if (property == null && method is "get" or "set" && DefaultIndexerAccessor(native, method == "set") is { } indexer)
            method = indexer;
        if (property is "index-get" or "index-set")
            method = DefaultIndexerAccessor(native, property == "index-set")
                ?? (method != null && DeclaresPublicMethodNamed(native, method) ? method : property == "index-set" ? "set_Item" : "get_Item");
        else if (property is "get" or "set")
        {
            if (method != null && MemberIsPropertyOrField(native, method))
                return new(ownerType, native, method, isStatic, property == "get" ? NativeCallKind.PropertyGet : NativeCallKind.PropertySet);
            method = KotlinPropertyAccessors.PhysicalName(method, property);
        }
        if (!isStatic && method != null && OperatorToNet.TryGetValue(method, out var nativeOperator)
            && DeclaresPublicStaticMethod(native, nativeOperator))
            return new(ownerType, native, nativeOperator, false, NativeCallKind.Operator);
        if (!isStatic && method == "compareTo" && DeclaresPublicMethodNamed(native, "CompareTo") && comparableCall) method = "CompareTo";
        return new(ownerType, native, method, isStatic, NativeCallKind.Method);
    }

    static FieldSelection SelectField(ReferenceMetadataIndex refs, TypeNode ownerType, string name, string method,
        bool companionCall, string visibility)
    {
        var owner = UnwrapFqn(ownerType);
        if (owner == null) return null;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        var physicalOwner = ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name);
        bool? companionStatic = companionCall && refs.TryCompanionIsStatic(bare, out var resolvedStatic) ? resolvedStatic : null;
        var native = companionStatic != null ? refs.ResolveRefType(physicalOwner, owner.Args?.Length ?? 0)
            : refs.ResolveNetType(physicalOwner, owner.Args?.Length ?? 0);
        if (native == null && method == "invoke" && refs.IsClrDelegate(owner)) native = ClrMemberResolution.ResolveOwnerType(owner, refs);
        if (companionCall && companionStatic == null)
        {
            native ??= refs.HasDotKtOwner(bare) ? refs.ResolveRefType(physicalOwner, owner.Args?.Length ?? 0) : null;
            if (native == null || !refs.TryKotlinRichEnumEntryField(bare, name, out var field))
                throw new InvalidOperationException($"DotKt companion field owner '{bare}' has no trusted companion carrier");
            companionStatic = true;
            name = field;
        }
        if (native == null)
        {
            if (companionCall) throw new InvalidOperationException($"companion field owner '{bare}' is absent from the selected references");
            return null;
        }
        if (name == null || !MemberIsPropertyOrField(native, name)) return null;
        if (visibility is "private" or "protected" or "internal")
        {
            var close = companionStatic == true || MemberIsStaticPropertyOrField(native, name);
            return new(close ? CloseStaticOwner(ownerType, native) : ownerType, name, close, true);
        }
        var isStatic = companionStatic ?? (native.IsAbstract && native.IsSealed || MemberIsStaticPropertyOrField(native, name));
        return new(isStatic ? CloseStaticOwner(ownerType, native) : ownerType, name, isStatic, false);
    }

    static bool CompanionEventStatic(ReferenceMetadataIndex refs, TypeNode owner)
    {
        var bare = owner is TypeNode.Fqn fqn ? ReferenceMetadataIndex.BareOwnerFqn(fqn.Name) : null;
        if (bare == null) throw new InvalidOperationException("companion event has no owner");
        if (!refs.TryCompanionIsStatic(bare, out var isStatic))
            throw new InvalidOperationException($"DotKt companion event owner '{bare}' has no trusted companion carrier");
        return isStatic;
    }

    static void ValidateSuspendCompanion(ReferenceMetadataIndex refs, string owner)
    {
        if (!refs.TryCompanionIsStatic(owner, out var isStatic) || isStatic)
            throw new InvalidOperationException($"external companion callable reference owner '{owner}' has no trusted instance carrier");
    }

    static TypeNode StaticEventOwner(ReferenceMetadataIndex refs, TypeNode type)
    {
        if (type is not TypeNode.Fqn owner) return null;
        var arity = owner.Args?.Length ?? 0;
        var physical = refs.ExactReflectedOwner(owner.Name, arity);
        var native = refs.ResolveNetType(physical, arity);
        return native == null ? null : CloseStaticOwner(new TypeNode.Fqn(physical, owner.Args), native);
    }
}
