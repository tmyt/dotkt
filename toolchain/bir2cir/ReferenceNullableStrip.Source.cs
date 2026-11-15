using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ReferenceNullableStrip
{
    internal static void Apply(BirGraphValue root, ValueTypeOracle isValue)
    {
        void Walk(BirGraphValue node)
        {
            if (node is null or IBirExecutablePayload) return;
            if (node is BirConstant { Type.HasValue: true, Type.Value: TypeNode.Nullable literal } constant
                && (constant.Value.IsNull || constant.Value.OrDefault() is BirLiteral.NullLiteral)
                && !IsValueInner(literal.Of, isValue))
                constant.Type = new TypeNode.Fqn("kotlin.Nothing");
            // Generated structural fields omit encoded source leaves, but
            // include structured signatures even when their frame is frozen
            // or foreign. That is the former whole-tree strip's exact domain.
            node.RewriteOwnFieldsInPlace((_, _, type) => Strip(type, isValue),
                (_, _, slot) => Walk(slot.Value));
        }
        Walk(root);
    }
}
