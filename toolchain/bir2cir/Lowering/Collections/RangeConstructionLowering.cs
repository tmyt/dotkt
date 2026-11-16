using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Value-position primitive ranges are realized after membership has consumed
// eligible endpoints; user range operators remain ordinary method calls.
static class RangeConstructionLowering
{
    public static BirFileDeclaration Apply(BirFileDeclaration root)
        => (BirFileDeclaration)SourceGraphRewrite.Rewrite(root, (node, _) => Rewrite(node));

    static BirGraphValue Rewrite(BirGraphValue node)
    {
        if (node is not BirCallInstance call || call.Method.OrDefault() is not ("rangeTo" or "rangeUntil")
            || call.OwnerType.OrDefault() is not TypeNode.Fqn owner || !call.Recv.HasValue
            || call.Args.OrDefault() is not { Length: 1 } args) return node;
        var range = owner.Name switch {
            "kotlin.Byte" or "kotlin.Short" or "kotlin.Int" => "kotlin.ranges.IntRange",
            "kotlin.Long" => "kotlin.ranges.LongRange",
            "kotlin.Char" => "kotlin.ranges.CharRange",
            _ => null,
        };
        if (range == null) return node;
        var endpoint = new TypeNode.Fqn(range switch {
            "kotlin.ranges.LongRange" => "kotlin.Long",
            "kotlin.ranges.CharRange" => "kotlin.Char",
            _ => "kotlin.Int",
        });
        IBirExpression end = args[0];
        if (call.Method.Value == "rangeUntil")
            end = new BirBinaryOperation {
                Op = "-", Lhs = OptionalField<IBirExpression>.Of(end),
                Rhs = OptionalField<IBirExpression>.Of(new BirConstant {
                    Type = new TypeNode.Fqn(owner.Name == "kotlin.Long" ? "kotlin.Long" : "kotlin.Int"),
                    Value = new BirLiteral.IntegerLiteral(1),
                }),
            };
        return new BirNew { Type = new TypeNode.Fqn(range),
            ArgTypes = new TypeNode[] { endpoint, endpoint }, Args = new[] { call.Recv.Value, end } };
    }
}
