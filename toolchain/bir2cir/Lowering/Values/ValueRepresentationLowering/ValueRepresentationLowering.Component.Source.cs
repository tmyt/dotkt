using DotKt.Bir;
using DotKt.Bir.Lowering;

sealed partial class ValueRepresentationLowering
{
    // This is an owning rewrite. The admitted runtime-type expression has one
    // occurrence, including any already-registered declaration nested within it.
    internal IBirExpression ComponentPredicate(TypeNode type, IBirExpression actualType) =>
        ComponentClassifier(type) is { } target
            ? HelperValueOwned(target, new[] { actualType },
                new TypeNode[] { new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType") },
                new TypeNode.Fqn("kotlin.Boolean"))
            : null;
}
