using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Source recognition's lexical walk, shared by passes that consume source type
// facts. Statement-vector boundaries come from the maintained owning schema.
static partial class SourceGraphRewrite
{
    internal static BirGraphValue Rewrite(BirGraphValue root,
        Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, BirGraphValue> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        return Walk(root, ImmutableDictionary.Create<string, TypeNode>(StringComparer.Ordinal));

        BirGraphValue Walk(BirGraphValue node, ImmutableDictionary<string, TypeNode> scope)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            var childScope = scope;
            node.VisitSourceParameters((name, type) => childScope = childScope.SetItem(name, type));
            node.VisitSourceChildren(slot => slot.Replace(Walk(slot.Value, childScope)), statements => {
                var current = childScope;
                for (var index = 0; index < statements.Length; index++)
                {
                    statements[index] = (IBirStatement)Walk((BirGraphValue)statements[index], current);
                    if (statements[index] is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                        current = current.SetItem(variable.Name.Value, variable.Type.Value);
                }
            });
            return rewrite(node, scope);
        }
    }
}
