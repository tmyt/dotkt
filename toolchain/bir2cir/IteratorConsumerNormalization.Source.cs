using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class IteratorConsumerNormalization
{
    const string Bridge = "kotlin.collections.ClrIteratorBridgeKt";
    static readonly TypeNode ObjT = new TypeNode.Fqn("object");
    // The member pass has already chosen the iterator protocol. Retain its
    // actual operands and normalize only the referenced consumer face.
    internal static void Apply(BirFileDeclaration root) => ProcessSource(root);

    static TypeNode IteratorReference(TypeNode element)
        => new TypeNode.Fqn("kotlin.collections.Iterator", new[] { element });

    static void ProcessSource(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirVariable { Name.HasValue: true, Init.HasValue: true } variable)
        {
            if (variable.Init.Value is BirCallStatic {
                Owner.HasValue: true, Method.HasValue: true, TypeArgs.HasValue: true,
            } bridge && bridge.Owner.Value is TypeNode.Fqn { Name: Bridge }
                && bridge.Method.Value == "iteratorOverEnumerable" && bridge.TypeArgs.Value is [var element])
                variable.Type = IteratorReference(element);
            else if (IteratorVarElem(variable.Type.OrDefault()) is (string head, TypeNode item)
                && InitializerOwner((BirGraphValue)variable.Init.Value) is { } owner
                && (owner.StartsWith("kotlin.", StringComparison.Ordinal)
                    || owner.StartsWith("dotkt$ClrH_kotlin_", StringComparison.Ordinal)))
            {
                if (item is TypeNode.Fqn { Args: { } } entry
                    && entry.Name is "kotlin.collections.MutableMap.MutableEntry" or "kotlin.collections.MutableMap$MutableEntry"
                    && variable.Init.Value is BirCallStatic {
                        Owner.HasValue: true, Method.HasValue: true, Args.HasValue: true,
                    } map && map.Owner.Value is TypeNode.Fqn { Name: "kotlin.collections.MapsKt" }
                    && map.Method.Value == "iterator" && map.Args.Value is [var receiver])
                {
                    var (key, value) = EntryKvArgs(entry);
                    variable.Init = new BirCallStatic {
                        Owner = new TypeNode.Fqn(Bridge), Method = "iteratorOverEnumerable",
                        Args = new IBirExpression[] { new BirCallStatic {
                            Owner = new TypeNode.Fqn("kotlin.collections.ClrMapDefaultsKt"), Method = "clrMapMutableEntries",
                            Sig = MemberCallSubstitution.MapHelperTypes("clrMapMutableEntries"),
                            Args = new[] { (IBirExpression)((BirGraphValue)receiver).DeepClone(),
                                MemberCallSubstitution.StaticMapPreferenceValue(new TypeNode.Fqn(
                                    "kotlin.collections.MutableMap", new[] { key, value }), null) },
                            TypeArgs = new[] { key, value },
                        } },
                        Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.collections.ClrEnumerable",
                            new TypeNode[] { new TypeNode.Tv("method", 0) }) },
                        TypeArgs = new[] { item },
                    };
                    variable.Type = IteratorReference(item);
                }
                else variable.Type = new TypeNode.Fqn(head, new[] { item });
            }
        }
        else if (node is BirCallInstance call && call.Method.OrDefault() is "hasNext" or "next"
            && IteratorDispatchElem(call.OwnerType.OrDefault()) is { } dispatchElement)
        {
            call.OwnerType = IteratorReference(dispatchElement);
            call.Virtual = true;
            call.Sig = Array.Empty<TypeNode>();
            call.Ret = call.Method.Value == "next" ? dispatchElement : new TypeNode.Fqn("kotlin.Boolean");
            if (call.Sty.HasValue && (!IrSanity.StampAgrees(call.Sty.Value, call.Ret.Value)
                || call.DynRet.HasValue && !IrSanity.StampAgrees(call.Sty.Value, call.DynRet.Value)))
                call.Sty = default;
        }
        // Preorder is deliberate: children include any newly authored map
        // bridge, and nested declarations retain the same traversal clock.
        node.VisitChildren(ProcessSource);
    }

    static string InitializerOwner(BirGraphValue node)
    {
        TypeNode owner = null, ownerType = null;
        node.VisitOwnTypeFields((field, type) => {
            if (field == "owner") owner = type;
            else if (field == "ownerType") ownerType = type;
        });
        return (owner as TypeNode.Fqn)?.Name ?? (ownerType as TypeNode.Fqn)?.Name;
    }

    static (TypeNode, TypeNode) EntryKvArgs(TypeNode.Fqn element)
    {
        var arguments = element.Args;
        return (arguments is { Length: >= 1 } && arguments[0] != null ? arguments[0] : ObjT,
                arguments is { Length: >= 2 } && arguments[1] != null ? arguments[1] : ObjT);
    }

    static (string, TypeNode)? IteratorVarElem(TypeNode type)
        => type is TypeNode.Fqn { Args: { Length: 1 } arguments } named
            && named.Name is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator"
                ? (named.Name, arguments[0]) : null;

    static TypeNode IteratorDispatchElem(TypeNode type)
        => type is TypeNode.Fqn { Args: { Length: 1 } arguments } named
            && named.Name is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator"
                ? arguments[0] : null;
}
