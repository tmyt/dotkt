using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PrimitiveOperatorLowering
{
    // The source graph owner supplies lexical timing and the two static-type
    // views. This consumer owns only primitive operator representation.
    internal static BirGraphValue Apply(BirGraphValue root, ReferenceMetadataIndex refs = null)
        => SourceGraphRewrite.Rewrite(root, (node, locals) =>
            TypedOperator(node, locals) is { } replacement ? (BirGraphValue)replacement : node);

    static IBirExpression TypedOperator(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> locals)
    {
        if (node is BirCallStatic call) return TypedIntrinsic(call, locals);
        if (node is not BirCallInstance memberCall
            || memberCall.OwnerType.OrDefault() is not TypeNode.Fqn owner
            || !memberCall.Method.HasValue) return null;
        var ownerName = ReferenceMetadataIndex.BareOwnerFqn(owner.Name);
        var member = memberCall.Method.Value;
        var args = memberCall.Args.OrEmpty();
        var receiver = memberCall.Recv.OrDefault();
        if (ownerName == "kotlin.String" && member == "plus" && args.Length == 1)
            return new BirConcat { Parts = new[] { CloneOperand(receiver), CloneOperand(args[0]) } };
        if (!PrimitiveOpFq.Contains(ownerName)) return null;
        if (args.Length == 1 && ArithOp.TryGetValue(member, out var symbol))
        {
            symbol = ArithmeticSymbol(ownerName, member, symbol);
            var right = CloneOperand(args[0]);
            if (symbol is "<<" or ">>" or ">>>")
                right = Binary("&", right, Integer(ShiftMask(ownerName)));
            return TypedDeclaredReturn(memberCall, ownerName, Binary(symbol, CloneOperand(receiver), right));
        }
        if (args.Length == 0 && UnaryOp.TryGetValue(member, out var unary))
            return TypedDeclaredReturn(memberCall, ownerName, new BirUnaryOperation {
                Op = unary, E = OptionalField<IBirExpression>.Of(CloneOperand(receiver)),
            });
        if (args.Length == 0 && member is "inc" or "dec")
            return TypedDeclaredReturn(memberCall, ownerName,
                Binary(member == "inc" ? "+" : "-", CloneOperand(receiver), Integer(1)));
        return null;
    }

    static IBirExpression TypedIntrinsic(BirCallStatic call, IReadOnlyDictionary<string, TypeNode> locals)
    {
        if (call.Owner.OrDefault() is not TypeNode.Fqn owner
            || ReferenceMetadataIndex.BareOwnerFqn(owner.Name) != IntrinsicOwner || !call.Method.HasValue) return null;
        var args = call.Args.OrEmpty();
        if (args.Length != 2) return null;
        var left = args[0];
        var right = args[1];
        var member = call.Method.Value;
        if (CompareOp.TryGetValue(member, out var comparison))
            return Binary(comparison, TypedCompareOperand(left, right, locals), TypedCompareOperand(right, left, locals));
        if (member == "EQEQ")
        {
            if (KotlinEqualityLowering.NullComparison(left, right) is { } nullCheck) return nullCheck;
            var leftSurface = BirGraphStaticType.Surface(left, locals);
            var rightSurface = BirGraphStaticType.Surface(right, locals);
            if (leftSurface is TypeNode.Fqn ls && EqEqPrimFq.Contains(ls.Name)
                && rightSurface is TypeNode.Fqn rs && EqEqPrimFq.Contains(rs.Name))
                return Binary("==", Cast(leftSurface, CloneOperand(left)), Cast(rightSurface, CloneOperand(right)));
            var leftValue = BirGraphStaticType.Value(left, locals);
            var rightValue = BirGraphStaticType.Value(right, locals);
            if (leftValue != null && rightValue != null)
            {
                if (FaithfulHints.IsNonNullFqn(leftValue, "kotlin.Double") && FaithfulHints.IsNonNullFqn(rightValue, "kotlin.Double"))
                    return FaithfulHints.FloatCall("clrDoubleEquals", CloneOperand(FaithfulHints.StripAnyCast(left)),
                        CloneOperand(FaithfulHints.StripAnyCast(right)));
                if (FaithfulHints.IsNonNullFqn(leftValue, "kotlin.Float") && FaithfulHints.IsNonNullFqn(rightValue, "kotlin.Float"))
                    return FaithfulHints.FloatCall("clrFloatEquals", CloneOperand(FaithfulHints.StripAnyCast(left)),
                        CloneOperand(FaithfulHints.StripAnyCast(right)));
                if (NullableFloatElem(leftValue) is string element && NullableFloatElem(rightValue) == element)
                    return TypedNullableFloatEquality(left, true, right, true, element, totalOrder: true);
            }
            return KotlinEqualityLowering.Call(left, right, nullSafe: true);
        }
        if (member == "EQEQEQ") return Binary("===", CloneOperand(left), CloneOperand(right));
        if (member == "ieee754equals")
        {
            var leftFloat = FloatOperand(BirGraphStaticType.Surface(left, locals));
            var rightFloat = FloatOperand(BirGraphStaticType.Surface(right, locals));
            if (leftFloat is { } lf && rightFloat is { } rf && (lf.nullable || rf.nullable))
                return TypedNullableFloatEquality(left, lf.nullable, right, rf.nullable,
                    lf.nullable ? lf.elem : rf.elem, totalOrder: false);
            return Binary("==", CloneOperand(left), CloneOperand(right));
        }
        return null;
    }

    static IBirExpression TypedCompareOperand(IBirExpression operand, IBirExpression other,
        IReadOnlyDictionary<string, TypeNode> locals)
    {
        var type = BirGraphStaticType.Surface(operand, locals);
        if (type is TypeNode.Nullable nullable && nullable.Of is TypeNode.Fqn named && ComparePrimFq.Contains(named.Name))
            return new BirNullableValue { Elem = nullable.Of, E = OptionalField<IBirExpression>.Of(CloneOperand(operand)) };
        if (type is TypeNode.Fqn { Name: "kotlin.Any" })
        {
            var otherType = BirGraphStaticType.Surface(other, locals);
            if (otherType != null && otherType is not TypeNode.Fqn { Name: "kotlin.Any" })
                return Cast(otherType, CloneOperand(operand));
        }
        return CloneOperand(operand);
    }

    // Both variants hoist each operand once. Only the both-present comparison
    // differs: structural equality uses total order; direct equality uses IEEE.
    static BirValueBlock TypedNullableFloatEquality(IBirExpression left, bool leftNullable,
        IBirExpression right, bool rightNullable, string elementName, bool totalOrder)
    {
        var leftName = FreshNullableEqualityTemporary();
        var rightName = FreshNullableEqualityTemporary();
        TypeNode element = new TypeNode.Fqn(elementName);
        BirLocal Local(string name) => new() { Name = name };
        BirVariable Variable(string name, IBirExpression init, bool nullable) => new() {
            Name = name, Type = nullable ? new TypeNode.Nullable(element) : element,
            Init = OptionalField<IBirExpression>.Of(CloneOperand(init)),
        };
        IBirExpression HasValue(string name, bool nullable) => nullable
            ? new BirNullableHasValue { Elem = element, E = Local(name) } : Boolean(true);
        IBirExpression Value(string name, bool nullable) => nullable
            ? new BirNullableValue { Elem = element, E = Local(name) } : Local(name);
        var present = totalOrder
            ? (IBirExpression)FaithfulHints.FloatCall(elementName == "kotlin.Double" ? "clrDoubleEquals" : "clrFloatEquals",
                Value(leftName, leftNullable), Value(rightName, rightNullable))
            : Binary("==", Value(leftName, leftNullable), Value(rightName, rightNullable));
        return new BirValueBlock {
            Stmts = new IBirStatement[] { Variable(leftName, left, leftNullable), Variable(rightName, right, rightNullable) },
            Result = new BirConditional {
                Cond = Binary("==", HasValue(leftName, leftNullable), HasValue(rightName, rightNullable)),
                Then = new BirConditional {
                    Cond = OptionalField<IBirExpression>.Of(HasValue(leftName, leftNullable)),
                    Then = OptionalField<IBirExpression>.Of(present), Else = Boolean(true),
                },
                Else = Boolean(false),
            },
        };
    }

    static IBirExpression TypedDeclaredReturn(BirCallInstance call, string owner, IBirExpression bare)
        => NarrowResultFq.Contains(owner) && call.DynRet.OrDefault() is TypeNode.Fqn declared
            ? new BirConvert { To = new TypeNode.Fqn(declared.Name), E = OptionalField<IBirExpression>.Of(bare) } : bare;

    static IBirExpression CloneOperand(IBirExpression expression)
        => expression == null ? null : (IBirExpression)((BirGraphValue)expression).DeepClone();
    static BirCast Cast(TypeNode type, IBirExpression expression)
        => new() { Type = type, E = OptionalField<IBirExpression>.Of(expression) };
    static BirBinaryOperation Binary(string op, IBirExpression left, IBirExpression right)
        => new() { Op = op, Lhs = OptionalField<IBirExpression>.Of(left), Rhs = OptionalField<IBirExpression>.Of(right) };
    static BirConstant Integer(int value)
        => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(value) };
    static BirConstant Boolean(bool value)
        => new() { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(value) };
}
