using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    static BirGraphValue BindProjectedFieldStorageSource(BirGraphValue node, SourceRewriteState state)
    {
        var (ownerType, name, receiver, value, result) = node switch {
            BirFieldGet field => (field.OwnerType.OrDefault(), field.Name.OrDefault(), field.Recv.OrDefault(),
                (IBirExpression)null, field.Ret.OrDefault() ?? field.MemberType.OrDefault() ?? field.Sty.OrDefault()),
            BirLateinitGet field => (field.OwnerType.OrDefault(), field.Name.OrDefault(), field.Recv.OrDefault(),
                (IBirExpression)null, field.Ret.OrDefault() ?? field.MemberType.OrDefault() ?? field.Sty.OrDefault()),
            BirFieldSetExpression field => (field.OwnerType.OrDefault(), field.Name.OrDefault(), field.Recv.OrDefault(),
                field.Value.OrDefault(), field.Ret.OrDefault() ?? field.MemberType.OrDefault()),
            BirSetField field => (field.OwnerType.OrDefault(), field.Name.OrDefault(), field.Recv.OrDefault(),
                field.Value.OrDefault(), field.MemberType.OrDefault()),
            _ => (null, null, null, null, null),
        };
        if (ownerType is not TypeNode.Fqn owner || name == null || receiver == null
            || node is BirFieldGet addressed && addressed._nativeFieldAddress.OrDefault() || SourceExactOuter(node)
            || receiver is BirThis || SourceExactOuter((BirGraphValue)receiver) && !state.SharedOuters.Contains(owner.Name)) return node;
        var setter = node is BirFieldSetExpression or BirSetField;
        var role = setter ? "set" : "get";
        if (state.Owners.TryGetValue(owner.Name, out var local))
        {
            if (!state.Declarations.Slots(local).Values.Any(row =>
                row.Declaration.PropertyAssociation.OrDefault() == local.Name + ":field:" + name
                && KotlinPropertyAccessors.TryIdentity(row.Declaration, out var property, out var accessor)
                && property == name && accessor == role)) return node;
        }
        else if (!state.References.TryStarProjectionField(owner, name, role, out _, out _, out _, out _, out _)) return node;
        var call = new BirCallInstance { OwnerType = BirGraphValue.CopyType(owner), Method = name,
            Recv = OptionalField<IBirExpression>.Of(receiver), Virtual = true,
            Args = setter ? new[] { value } : Array.Empty<IBirExpression>(),
            Ret = BirGraphValue.CopyType(setter ? new TypeNode.Fqn("kotlin.Unit") : result),
            PropertyName = name, PropertyAccessor = role };
        var bound = BindInheritedStarMemberSource(call, state);
        var selected = bound is BirCast cast ? cast.E.OrDefault() as BirCallInstance : bound as BirCallInstance;
        if (selected?.OwnerType.OrDefault() is not TypeNode.Fqn physical
            || !IsExistentialPhysicalCarrier(physical, state.Owners, state.References)) return node;
        // The receiver and assigned value move into the selected call. A
        // statement write remains a statement; no executable operand is cloned.
        return node is BirSetField ? new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)bound) } : bound;
    }

    static BirGraphValue BindStarFieldThroughCanonicalGetterSource(BirGraphValue node, SourceRewriteState state)
    {
        if (node is not BirFieldGet field || field.Outer.OrDefault() || field._nativeFieldAddress.OrDefault()) return node;
        var copyDefault = field.DataClassCopyDefault.OrDefault();
        var generatedEquals = field.GeneratedEqualsFieldRead.OrDefault();
        field.DataClassCopyDefault = default;
        field.GeneratedEqualsFieldRead = default;
        if (field.Name.OrDefault() is not string name || field.OwnerType.OrDefault() is not TypeNode.Fqn { Args: { } } owner) return node;
        (Owner Owner, SourceSlot Slot)? canonical = null;
        if (state.Owners.TryGetValue(owner.Name, out var start) && start.Needed)
            canonical = FindDeclaringSourceOwner(start, name, "get", 0, 0, Array.Empty<TypeNode>(), null, state.Demand, state.Declarations);
        var exact = canonical is { } local && local.Owner.Name == owner.Name
            && IsCanonicalSourceFieldGetter(local.Slot.Declaration, owner.Name, name);
        var generatedExact = generatedEquals && canonical is { } equality && equality.Owner.Name == owner.Name
            && IsCanonicalSourceFieldGetter(equality.Slot.Declaration, owner.Name, name, allowVirtual: true);
        if (!exact && !copyDefault && !generatedExact) return node;
        var result = field.MemberType.OrDefault() ?? field.Sty.OrDefault() ?? canonical?.Slot.Declaration.Ret.OrDefault();
        if (!field.Recv.HasValue || result == null) return node;
        var receiver = field.Recv.Value;
        // Tentative binding must not stamp the live field receiver when the
        // getter selection fails. A local read has no declaration children.
        if (generatedEquals && receiver is BirLocal read && state.Owners.TryGetValue(owner.Name, out var equalityOwner) && equalityOwner.Needed)
            receiver = read with { Sty = new TypeNode.Fqn(equalityOwner.ErasedName) };
        var call = new BirCallInstance { OwnerType = BirGraphValue.CopyType(owner), Method = name,
            Recv = OptionalField<IBirExpression>.Of(receiver), Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
            Ret = BirGraphValue.CopyType(result), Virtual = false, PropertyName = name, PropertyAccessor = "get",
            Sty = field.Sty.HasValue ? BirGraphValue.CopyType(field.Sty.Value) : default };
        var bound = BindInheritedStarMemberSource(call, state, alignResult: false);
        if (bound is not BirCallInstance selected || selected.OwnerType.OrDefault() is not TypeNode.Fqn physical
            || !IsExistentialPhysicalCarrier(physical, state.Owners, state.References)) return node;
        if (canonical is { } getter && getter.Slot.Declaration.Ret.OrDefault() is TypeNode getterResult)
        {
            var physicalResult = EraseOwnerTv(getterResult, state.Owners, state.References);
            selected.Ret = BirGraphValue.CopyType(physicalResult);
            if (field.Sty.HasValue) selected.Sty = BirGraphValue.CopyType(physicalResult);
        }
        return selected;
    }

    static bool IsCanonicalSourceFieldGetter(BirFunctionDeclaration getter, string owner, string fieldName, bool allowVirtual = false)
        => (allowVirtual || !getter.Virtual.OrDefault()) && getter.Body.HasValue
            && getter.Body.Value.Where(statement => statement is not BirSourcePoint).Take(2).ToArray() is [BirReturn statement]
            && CanonicalSourceGetterField(statement.Value.OrDefault()) is { } field
            && field.Name.OrDefault() == fieldName && field.OwnerType.OrDefault() is TypeNode.Fqn fieldOwner
            && fieldOwner.Name == owner && field.Recv.OrDefault() is BirThis;

    static BirFieldGet CanonicalSourceGetterField(IBirExpression value)
    {
        if (value is BirFieldGet direct) return direct;
        if (value is not BirValueBlock block || block.Stmts.OrDefault() is not [BirVariable variable]
            || block.Result.OrDefault() is not BirConditional conditional
            || variable.Name.OrDefault() is not string name || variable.Init.OrDefault() is not BirFieldGet field
            || conditional.Cond.OrDefault() is not BirUnaryOperation not || not.Op.OrDefault() != "!"
            || not.E.OrDefault() is not BirBinaryOperation equals
            || equals.Op.OrDefault() != "==="
            || SourceReferenceGuardOperand(equals.Lhs.OrDefault()) is not BirLocal lhs || lhs.Name.OrDefault() != name
            || SourceReferenceGuardOperand(equals.Rhs.OrDefault()) is not BirConstant rhs
            || rhs.Value.OrDefault() is not (null or BirLiteral.NullLiteral)
            || conditional.Then.OrDefault() is not BirLocal then || then.Name.OrDefault() != name
            || conditional.Else.OrDefault() is not BirThrowExpression) return null;
        return field;
    }

    static IBirExpression SourceReferenceGuardOperand(IBirExpression operand)
    {
        if (operand is not BirCast cast) return operand;
        var type = cast.Type.OrDefault();
        if (type is TypeNode.Nullable nullable) type = nullable.Of;
        return type is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object" } ? cast.E.OrDefault() : operand;
    }
}
