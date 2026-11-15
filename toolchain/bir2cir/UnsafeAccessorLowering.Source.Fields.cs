using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    sealed record SourceFieldEdge(TypeNode Owner, string Name, string Visibility, TypeNode MemberType,
        BirTypeParameter[] OwnerParameters, IBirExpression Receiver, IBirExpression Value, TypeNode Return,
        TypeNode Stamp, bool Static, bool Volatile);
    static SourceFieldEdge SourceFieldEdgeOf(BirGraphValue node) => node switch {
        BirFieldGet n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), n.Recv.OrDefault(), null, n.Ret.OrDefault(), n.Sty.OrDefault(), false, n.Volatile.OrDefault()),
        BirSetField n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), n.Recv.OrDefault(), n.Value.OrDefault(), null, null, false, n.Volatile.OrDefault()),
        BirFieldSetExpression n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), n.Recv.OrDefault(), n.Value.OrDefault(), n.Ret.OrDefault(), null, false, n.Volatile.OrDefault()),
        BirStaticFieldGet n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), null, null, n.Ret.OrDefault(), n.Sty.OrDefault(), true, n.Volatile.OrDefault()),
        BirStaticFieldSet n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), null, n.Value.OrDefault(), null, null, true, n.Volatile.OrDefault()),
        BirLateinitGet n => new(n.OwnerType.OrDefault(), n.Name.OrDefault(), n.MemberVisibility.OrDefault(), n.MemberType.OrDefault(),
            n.MemberOwnerTypeParams.OrDefault(), n.Recv.OrDefault(), n.Value.OrDefault(), n.Ret.OrDefault(), n.Sty.OrDefault(), false, n.Volatile.OrDefault()),
        _ => throw new InvalidOperationException("not an UnsafeAccessor field edge"),
    };
    static BirGraphValue RewriteSourceField(BirGraphValue node, SourceHost caller, SourceAccessContext context,
        IReadOnlyDictionary<string, TypeNode> scope)
    {
        var edge = SourceFieldEdgeOf(node);
        DropSourceMemberFacts(node);
        if (edge.Owner is not TypeNode.Fqn owner || edge.Name == null) return node;
        if (owner.Args is not { Length: > 0 } && edge.Receiver is BirGraphNode receiver
            && BirNodeType.Of(receiver) is TypeNode.Fqn constructed && constructed.Name == owner.Name && constructed.Args is { Length: > 0 })
            owner = constructed;
        context.Hosts.TryGetValue(owner.Name, out var host);
        if (host != null && SourcePrivateAccess(caller.Name, host.Name, context.Hosts)) return node;
        BirFieldDeclaration target = null;
        if (host != null)
        {
            target = host.LookupFields.SingleOrDefault(f => f.Name.OrDefault() == edge.Name);
            if (target == null || target.Vis.OrDefault() is not ("private" or "protected")) return node;
        }
        else if (edge.Visibility is not ("private" or "protected" or "internal")) return node;
        var isStatic = edge.Static || target?.Static.OrDefault() == true;
        var frontendType = edge.MemberType;
        var ownerParameters = edge.OwnerParameters;
        MemberRefNode selected = null;
        var isPublic = false;
        if (target == null && context.References != null)
        {
            selected = ClrMemberResolution.ResolveFieldForUnsafeAccessor(context.References, owner, edge.Name, isStatic,
                out BirTypeParameter[] referencedParameters, out isPublic);
            if (selected != null)
            {
                owner = selected.DeclaringType as TypeNode.Fqn
                    ?? throw new InvalidOperationException("UnsafeAccessor field has no exact declaring owner");
                frontendType = selected.ReturnType;
                while (frontendType is TypeNode.Mod modifier) frontendType = modifier.Of;
                ownerParameters = referencedParameters;
            }
        }
        var fieldType = target?.Type.OrDefault() ?? frontendType ?? edge.Return ?? edge.Stamp;
        if (fieldType == null) return node;
        if (isPublic || context.Allows(caller, edge.Receiver, owner, target?.Vis.OrDefault() ?? edge.Visibility,
            isStatic, scope, new[] { fieldType }))
        {
            if (selected != null) SetSourceSelectedField(node, owner, selected);
            return node;
        }
        var actualType = owner.Args is { Length: > 0 } ? SubstituteOwnerSlots(fieldType, owner.Args) : fieldType;
        var key = $"{caller.Name}|field|{owner.Name}|{edge.Name}|{isStatic}|{SourceTypeKey(fieldType)}";
        var nullableReturn = target?.NullableGeneric.OrDefault() is { } nullable ? new TypeNode.ByRef(nullable) : null;
        var definition = EnsureSourceAccessor(caller, context, key, edge.Name, isStatic ? 4 : 3, owner,
            SourceOwnerParameters(host, ownerParameters, owner, context.References), null,
            new TypeNode.ByRef(fieldType), Array.Empty<TypeNode>(), true, nullableReturn: nullableReturn, ownershipKnown: target != null);
        var pointer = SourceAccessorCall(caller, definition, owner,
            new IBirExpression[] { isStatic ? new BirDefaultValue { Type = CopySourceType(owner) } : edge.Receiver }, new TypeNode.ByRef(actualType));
        var volatileAccess = edge.Volatile || target?.Volatile.OrDefault() == true;
        if (node is BirFieldGet or BirStaticFieldGet or BirLateinitGet)
        {
            var read = new BirByRefLoad { Ptr = OptionalField<IBirExpression>.Of(pointer), Elem = CopySourceType(actualType) };
            if (volatileAccess) read.Volatile = true;
            if (node is not BirLateinitGet lateinit) return read;
            var replacement = new BirLateinitGet { Name = edge.Name, Value = OptionalField<IBirExpression>.Of(read), Sty = CopySourceType(actualType) };
            if (lateinit.LateinitSourceName.HasValue) replacement.LateinitSourceName = lateinit.LateinitSourceName;
            return replacement;
        }
        var store = new BirByRefStore { Ptr = OptionalField<IBirExpression>.Of(pointer), Elem = CopySourceType(actualType), Value = OptionalField<IBirExpression>.Of(edge.Value) };
        if (volatileAccess) store.Volatile = true;
        return node is BirSetField ? new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(store) } : store;
    }

    static void SetSourceSelectedField(BirGraphValue node, TypeNode owner, MemberRefNode selected)
    {
        var reference = BirMemberReference.FromPhysical(selected);
        switch (node)
        {
            case BirFieldGet n: n.OwnerType = CopySourceType(owner); n.MemberRef = reference; n.ResolvedMemberReturn = CopySourceType(selected.ReturnType); break;
            case BirSetField n: n.OwnerType = CopySourceType(owner); n.MemberRef = reference; n.ResolvedMemberReturn = CopySourceType(selected.ReturnType); break;
            case BirFieldSetExpression n: n.OwnerType = CopySourceType(owner); n.MemberRef = reference; n.ResolvedMemberReturn = CopySourceType(selected.ReturnType); break;
            case BirStaticFieldGet n: n.OwnerType = CopySourceType(owner); n.FieldRef = reference; break;
            case BirStaticFieldSet n: n.OwnerType = CopySourceType(owner); n.FieldRef = reference; break;
            case BirLateinitGet n: n.OwnerType = CopySourceType(owner); n.FieldRef = reference; break;
        }
    }

    static BirGraphValue DropSourceMemberFacts(BirGraphValue node, bool preserveConstructorSignature = false)
    {
        switch (node)
        {
            case BirCallInstance n: n.MemberVisibility = default; n.MemberOwnerTypeParams = default; n.MemberMethodTypeParams = default; n.MemberReturnType = default; n.MemberSignature = default; break;
            case BirCallStatic n: n.MemberVisibility = default; n.MemberOwnerTypeParams = default; n.MemberMethodTypeParams = default; n.MemberReturnType = default; n.MemberSignature = default; break;
            case BirConstrainedCall n: n.MemberVisibility = default; n.MemberOwnerTypeParams = default; n.MemberMethodTypeParams = default; n.MemberReturnType = default; n.MemberSignature = default; break;
            case BirNewBoundDelegate n: n.MemberVisibility = default; n.MemberOwnerTypeParams = default; n.MemberMethodTypeParams = default; n.MemberReturnType = default; n.MemberSignature = default; break;
            case BirNew n:
                n.MemberVisibility = default; n.MemberOwnerTypeParams = default; n.MemberMethodTypeParams = default; n.MemberReturnType = default;
                if (!preserveConstructorSignature) n.MemberSignature = default;
                break;
            case BirNewClr n: if (!preserveConstructorSignature) n.MemberSignature = default; break;
            case BirFieldGet n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirSetField n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirFieldSetExpression n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirStaticFieldGet n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirStaticFieldSet n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirLateinitGet n: n.MemberVisibility = default; n.MemberType = default; n.MemberOwnerTypeParams = default; break;
            case BirEventGet n: n.MemberVisibility = default; break;
            case BirEventAdd n: n.MemberVisibility = default; break;
            case BirEventRemove n: n.MemberVisibility = default; break;
        }
        return node;
    }
}
