using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Resolve the lateinit null-check's failure path to an ordinary construction.
// The same exact constructor binder handles it; ilemit only evaluates and throws.
static partial class LateinitGetLowering
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        foreach (var root in roots) Walk(root);

        static void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitChildSlots(slot => Walk(slot.Value));
            if (node is not BirLateinitGet read || read.Exception.HasValue) return;
            var name = read.LateinitSourceName.OrDefault() ?? read.Name.OrDefault()
                ?? throw new InvalidOperationException("lateinitGet has no property name");
            var stringType = new TypeNode.Fqn("kotlin.String");
            read.Exception = OptionalField<IBirExpression>.Of(new BirNew {
                Type = new TypeNode.Fqn("kotlin.UninitializedPropertyAccessException"),
                ArgTypes = new TypeNode[] { stringType },
                Args = new IBirExpression[] { new BirConstant {
                    Type = stringType,
                    Value = new BirLiteral.TextLiteral($"lateinit property {name} has not been initialized"),
                } },
            });
            read.LateinitSourceName = default;
        }
    }
}
