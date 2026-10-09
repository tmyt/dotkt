using System.Linq;
using DotKt.Bir;

// Signature modifiers distinguish metadata declarations, never CLR stack values.
// Preserve the original signature for linkage; use this projection only in value-flow analysis.
static class SignatureValueTypes
{
    internal static TypeNode Of(TypeNode type) => type switch
    {
        TypeNode.Mod modifier => Of(modifier.Of),
        TypeNode.Fqn named when named.Args != null => new TypeNode.Fqn(named.Name, named.Args.Select(Of).ToArray()),
        TypeNode.Nullable nullable => new TypeNode.Nullable(Of(nullable.Of)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(Of(oblivious.Of)),
        TypeNode.Array array => new TypeNode.Array(Of(array.Elem), array.Rank, array.SzArray),
        TypeNode.ByRef reference => new TypeNode.ByRef(Of(reference.Of)),
        TypeNode.Ptr pointer => new TypeNode.Ptr(Of(pointer.Of)),
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, Of(projection.Of)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend, Of(function.Ret), function.Params.Select(Of).ToArray(),
            function.Recv == null ? null : Of(function.Recv), function.Clr, function.Ctx?.Select(Of).ToArray()),
        _ => type,
    };
}
