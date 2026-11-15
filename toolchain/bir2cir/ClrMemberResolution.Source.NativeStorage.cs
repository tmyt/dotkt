using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    static void ResolveNativeOverrideSource(BirFunctionDeclaration method)
    {
        var owner = method.PendingOverrideOwner.OrDefault() as TypeNode.Fqn;
        var implementation = method.Name.OrDefault();
        var name = method.PendingOverrideMember.OrDefault() ?? implementation;
        var rawReturn = method.PendingOverrideReturn.OrDefault();
        var result = rawReturn == null ? null : BirTypeLowering.CanonicalPhysicalSlotType(rawReturn);
        if (owner == null || implementation == null || name == null || !method.Params.HasValue) return;
        // This is the base declaration name, not the eventual implementation's
        // allocated MethodDef name. Freeze it before validating the descriptor.
        method.PendingOverrideMember = name;
        if (result == null)
            throw new InvalidOperationException($"bir2cir: override '{owner.Name}.{name}' is missing the exact base return descriptor");
        var open = ResolveOwnerType(owner) ?? throw new InvalidOperationException(
            $"bir2cir: override base owner '{owner.Name}' does not resolve to a .NET type");
        var arguments = method.Params.Value.Select((parameter, index) => parameter?.Type.OrDefault()
            ?? throw new InvalidOperationException($"bir2cir: override '{owner.Name}.{name}' param #{index} has an unreadable type node")).ToList();
        method.ClrOverrideRef = BirMemberReference.FromPhysical(SelectNativeOverrideBase(owner, open, name, arguments, result));
        method.RequiresClrOverride = true;
        method.PendingOverrideOwner = default;
        method.PendingOverrideMember = default;
        method.PendingOverrideReturn = default;
    }

    static void ResolveNativeStorageSource(BirGraphValue node)
    {
        switch (node)
        {
            case BirPropertyGet read:
                WriteNativeStorage(read, SelectNativePropertyStorage(read.Type.OrDefault(), read.Name.OrDefault(),
                    false, read.Static.OrDefault(), read.Super.OrDefault()));
                break;
            case BirPropertySet write:
                var property = SelectNativePropertyStorage(write.Type.OrDefault(), write.Name.OrDefault(),
                    true, write.Static.OrDefault(), write.Super.OrDefault());
                WriteNativeStorage(write, property);
                MarkNativeWrittenSlot(write.Value.OrDefault(), property.WrittenSlot);
                break;
            case BirFieldGet read:
                if (HasNativeSelectedField(read.MemberRef.OrDefault(), false, null)) break;
                if (SelectNativeFieldStorage(read.OwnerType.OrDefault(), read.Name.OrDefault(), false) is { } field)
                    WriteNativeStorage(read, field);
                break;
            case BirFieldSetExpression write:
                if (HasNativeSelectedField(write.MemberRef.OrDefault(), true, write.Value.OrDefault())) break;
                if (SelectNativeFieldStorage(write.OwnerType.OrDefault(), write.Name.OrDefault(), true) is { } fieldWrite)
                {
                    WriteNativeStorage(write, fieldWrite);
                    MarkNativeWrittenSlot(write.Value.OrDefault(), fieldWrite.WrittenSlot);
                }
                break;
            case BirSetField write:
                if (HasNativeSelectedField(write.MemberRef.OrDefault(), true, write.Value.OrDefault())) break;
                if (SelectNativeFieldStorage(write.OwnerType.OrDefault(), write.Name.OrDefault(), true) is { } statementWrite)
                {
                    WriteNativeStorage(write, statementWrite);
                    MarkNativeWrittenSlot(write.Value.OrDefault(), statementWrite.WrittenSlot);
                }
                break;
            case BirEventAdd add:
                ResolveNativeEventSource(add);
                break;
            case BirEventRemove remove:
                ResolveNativeEventSource(remove);
                break;
            case BirStaticFieldGet read:
                if (!read.FieldRef.IsPresent && read.OwnerType.OrDefault() is TypeNode.Fqn readOwner
                    && read.Name.OrDefault() is string readName && StaticFieldReference(readOwner, readName) is { } readRef)
                    read.FieldRef = BirMemberReference.FromPhysical(readRef);
                break;
            case BirStaticFieldSet write:
                if (!write.FieldRef.IsPresent && write.OwnerType.OrDefault() is TypeNode.Fqn writeOwner
                    && write.Name.OrDefault() is string writeName && StaticFieldReference(writeOwner, writeName) is { } writeRef)
                    write.FieldRef = BirMemberReference.FromPhysical(writeRef);
                break;
            // clrStaticField has only the already-authored FieldRef, with no
            // unresolved owner/name producer in the current owning vocabulary.
        }
    }

    static bool HasNativeSelectedField(BirMemberReference selected, bool write, IBirExpression value)
    {
        if (selected?.Kind.OrDefault() != MemberRefNode.Kinds.Field) return false;
        if (write) MarkNativeWrittenSlot(value, SupertypeGraph.SubstOwnerTvs(
            SignatureValueTypes.Of(selected.ReturnType.OrDefault()),
            (selected.DeclaringType.OrDefault() as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>()));
        // Exact direct family storage is already authoritative. In particular,
        // do not overwrite its foreign-return presence or search a property.
        return true;
    }

    static void MarkNativeWrittenSlot(IBirExpression value, TypeNode slot)
    {
        if (value is BirGraphValue expression) MarkDelegateSlot(expression, slot, _refs, _localTypes);
    }

    static bool NativeLocalEvent(bool local, TypeNode delegateType, string accessor, TypeNode owner, string dispatch)
    {
        if (!local) return false;
        if (delegateType == null)
            throw new InvalidOperationException("bir2cir: local clrEvent is missing its bound delegate declaration");
        if (accessor == null || owner == null || dispatch == null)
            throw new InvalidOperationException("bir2cir: local clrEvent is missing its bound accessor identity");
        return true;
    }

    static BirCast NativeEventHandler(OptionalField<IBirExpression> handler, TypeNode target)
        // The selected accessor owns the stack view, not the delegate value's
        // identity. MOVE the actual operand under the new checked view.
        => new() { Type = target, E = handler.IsPresent ? handler : OptionalField<IBirExpression>.Null };

    static void ResolveNativeEventSource(BirEventAdd node)
    {
        if (NativeLocalEvent(node.LocalAccessor.OrDefault(), node.DelegateType.OrDefault(), node.Accessor.OrDefault(),
            node.AccessorOwner.OrDefault(), node.Dispatch.OrDefault())) return;
        var selected = SelectNativeEventStorage(node.Type.OrDefault(), node.Event.OrDefault(), node.Static.OrDefault(), true);
        node.Type = selected.Owner; node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference);
        node.Handler = NativeEventHandler(node.Handler, selected.WrittenSlot);
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void ResolveNativeEventSource(BirEventRemove node)
    {
        if (NativeLocalEvent(node.LocalAccessor.OrDefault(), node.DelegateType.OrDefault(), node.Accessor.OrDefault(),
            node.AccessorOwner.OrDefault(), node.Dispatch.OrDefault())) return;
        var selected = SelectNativeEventStorage(node.Type.OrDefault(), node.Event.OrDefault(), node.Static.OrDefault(), false);
        node.Type = selected.Owner; node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference);
        node.Handler = NativeEventHandler(node.Handler, selected.WrittenSlot);
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void WriteNativeStorage(BirPropertyGet node, NativeStorageSelection selected)
    {
        node.Type = selected.Owner; node.Member = selected.Member;
        if (selected.Accessor != null) node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference); node.DeclarationId = default;
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void WriteNativeStorage(BirPropertySet node, NativeStorageSelection selected)
    {
        node.Type = selected.Owner; node.Member = selected.Member;
        if (selected.Accessor != null) node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference); node.DeclarationId = default;
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void WriteNativeStorage(BirFieldGet node, NativeStorageSelection selected)
    {
        node.OwnerType = selected.Owner;
        if (selected.Member != null) node.Member = selected.Member;
        if (selected.Accessor != null) node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference);
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void WriteNativeStorage(BirFieldSetExpression node, NativeStorageSelection selected)
    {
        node.OwnerType = selected.Owner;
        if (selected.Member != null) node.Member = selected.Member;
        if (selected.Accessor != null) node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference);
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }

    static void WriteNativeStorage(BirSetField node, NativeStorageSelection selected)
    {
        node.OwnerType = selected.Owner;
        if (selected.Member != null) node.Member = selected.Member;
        if (selected.Accessor != null) node.Accessor = selected.Accessor;
        node.MemberRef = BirMemberReference.FromPhysical(selected.Reference);
        node.ResolvedMemberReturn = selected.DeclaredReturn;
        if (selected.Dispatch != null) node.Dispatch = selected.Dispatch;
    }
}
