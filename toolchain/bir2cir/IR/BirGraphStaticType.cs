using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Source-surface policy over the shared owning node-local deriver. Only lexical
// locals and the surface representation of arrays belong to this reader.
static class BirGraphStaticType
{
    // Recognition asks for the value below a source cast, rather than its
    // declared cast surface. Keep nullable-value extraction's exact element.
    internal static TypeNode Value(IBirExpression node, IReadOnlyDictionary<string, TypeNode> locals)
        => node switch {
            BirCast { E.HasValue: true } cast => Value(cast.E.Value, locals),
            BirNullableValue { E.HasValue: true } value => value.Elem.OrDefault() ?? Value(value.E.Value, locals),
            _ => Surface(node, locals),
        };

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
        TypeNode Array(TypeNode element) => element == null ? null : new TypeNode.Fqn("kotlin.Array", new[] { element });
        return node switch {
            BirLocal local => local.Name.HasValue && locals.TryGetValue(local.Name.Value, out var declared) ? declared : null,
            BirNewArray value => Array(value.Elem.OrDefault()),
            BirNewArrayInit value => Array(value.Elem.OrDefault()),
            BirNewArraySized value => Array(value.Elem.OrDefault()),
            BirSpreadConcat value => Array(value.Elem.OrDefault()),
            BirEnumValues value => Array(value.Type.OrDefault()),
            _ => BirNodeType.Of(node as BirGraphNode, value => Surface(value, locals),
                name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null),
        };
    }
}
