using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The owning graph's node-local facts, with the same lexical scope and result
// stamp precedence as StaticType.Surface. No reference/member resolution occurs.
static class BirGraphStaticType
{
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode String = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode Nothing = new TypeNode.Fqn("kotlin.Nothing");

    internal static TypeNode Surface(IBirExpression node, IReadOnlyDictionary<string, TypeNode> locals)
        => Surface(node as BirGraphValue, locals);

    internal static TypeNode Surface(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> locals)
    {
        if (node == null) return null;
        if (node is BirValueBlock block)
        {
            if (block.Type.HasValue) return block.Type.Value;
            var inner = new Dictionary<string, TypeNode>(locals, StringComparer.Ordinal);
            foreach (var statement in block.Stmts.OrEmpty())
                if (statement is BirVariable variable && variable.Name.HasValue && variable.Type.HasValue)
                    inner[variable.Name.Value] = variable.Type.Value;
            return Surface(block.Result.OrDefault(), inner);
        }
        if (node.ResultTypeStamp is { } stamp) return stamp;
        TypeNode Read(IBirExpression expression) => Surface(expression, locals);
        TypeNode Array(TypeNode element) => element == null ? null : new TypeNode.Fqn("kotlin.Array", new[] { element });
        TypeNode Nullable(TypeNode element) => element == null ? null : new TypeNode.Nullable(element);
        TypeNode Closure(TypeNode function, TypeNode closure) => function
            ?? (closure is TypeNode.Fqn named ? new TypeNode.Fqn(named.Name) : null);
        return node switch {
            BirLocal local => local.Name.HasValue && locals.TryGetValue(local.Name.Value, out var declared) ? declared : null,
            BirConstant value => value.Type.OrDefault(),
            BirCast cast => cast.Type.OrDefault(),
            BirSamConversion sam => sam.Type.OrDefault(),
            BirNew value => value.Type.OrDefault(),
            BirNewClr value => value.Type.OrDefault(),
            BirVariable variable => variable.Type.OrDefault(),
            BirEnumValue value => value.Type.OrDefault(),
            BirEnumBits value => value.Type.OrDefault(),
            BirEnumParse value => value.Type.OrDefault(),
            BirDefaultValue value => value.Type.OrDefault(),
            BirNullableValue value => value.Elem.OrDefault(),
            BirSafeValueCast value => value.Elem.OrDefault(),
            BirNullableWrap value => Nullable(value.Elem.OrDefault()),
            BirNullableNull value => Nullable(value.Elem.OrDefault()),
            BirConditional conditional => Conditional(conditional, Read),
            BirCallEvaluation call => call.Type.OrDefault() ?? Read(call.Expr.OrDefault()),
            BirStackGet read => read.Elem.OrDefault(),
            BirByRefLoad read => read.Elem.OrDefault(),
            BirArrayGet read => read.Elem.OrDefault() ?? NodeType.ElementOf(Read(read.Array.OrDefault()),
                name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null),
            BirConvert convert => convert.To.OrDefault(),
            BirBinaryOperation binary => binary.Op.OrDefault() is "===" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"
                ? Bool : Read(binary.Lhs.OrDefault()) ?? Read(binary.Rhs.OrDefault()),
            BirUnaryOperation unary => unary.Op.OrDefault() == "!" ? Bool : Read(unary.E.OrDefault()),
            BirObjectEquals or BirIsInstance or BirInstanceOrNull or BirNullableHasValue => Bool,
            BirThrowExpression or BirReturnExpression => Nothing,
            BirArrayLength or BirEnumOrdinal => Int,
            BirConcat or BirEnumName => String,
            BirNewArray value => Array(value.Elem.OrDefault()),
            BirNewArrayInit value => Array(value.Elem.OrDefault()),
            BirNewArraySized value => Array(value.Elem.OrDefault()),
            BirSpreadConcat value => Array(value.Elem.OrDefault()),
            BirEnumValues value => Array(value.Type.OrDefault()),
            BirClassReference or BirRuntimeClass => new TypeNode.Fqn("System.Type"),
            BirStackAsSpan value => value.Elem.HasValue ? new TypeNode.Fqn("System.Span", new[] { value.Elem.Value }) : null,
            BirStackAllocation => new TypeNode.Fqn("dotkt$stackptr"),
            BirNewClosure value => Closure(value.FuncType.OrDefault(), value.ClosureType.OrDefault()),
            BirNewSuspendLambda value => value.FuncType.OrDefault(),
            BirNewDelegate value => value.FuncType.OrDefault(),
            BirNewBoundDelegate value => value.FuncType.OrDefault(),
            BirBoundClrDelegate value => value.FuncType.OrDefault(),
            BirClrStaticDelegate value => value.FuncType.OrDefault(),
            BirNewSam value => value.SamType.OrDefault(),
            BirDelegateInvoke value => (value.FuncType.OrDefault() as TypeNode.Fn)?.Ret,
            BirObjectMethod value => value.Method.OrDefault() switch {
                "toString" or "ToString" => String,
                "hashCode" or "GetHashCode" => Int,
                "equals" or "Equals" => Bool,
                _ => null,
            },
            _ => null,
        };
    }

    static TypeNode Conditional(BirConditional conditional, Func<IBirExpression, TypeNode> read)
    {
        if (conditional.Type.HasValue) return conditional.Type.Value;
        var then = read(conditional.Then.OrDefault());
        return then != null && !NodeType.IsNothing(then) ? then : read(conditional.Else.OrDefault()) ?? then;
    }
}
