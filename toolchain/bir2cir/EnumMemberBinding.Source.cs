using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumMemberBinding
{
    public static BirFileDeclaration Apply(BirFileDeclaration root, HashSet<string> basicEnums)
    {
        Walk(root);
        return root;

        BirGraphValue Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            node = Rebind(node);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        BirGraphValue Rebind(BirGraphValue node)
        {
            if (node is not BirCallInstance { Method.HasValue: true, Recv.HasValue: true } call) return node;
            var owner = (call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name;
            if (owner == "kotlin.Enum" && AnySlotRebind.IsVirtualToString(call))
                return AnySlotRebind.FromCall(call, consumeAnySlot: true);
            if (owner == "kotlin.Enum" && call.Method.Value == "ordinal" && call.Prop.OrDefault() == "get")
                return new BirCallStatic {
                    Owner = new TypeNode.Fqn(RuntimeOwner), Method = "kotlinEnumOrdinal",
                    Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }, Ret = new TypeNode.Fqn("kotlin.Int"),
                    Args = new[] { call.Recv.Value },
                };
            return owner != null && basicEnums.Contains(owner)
                && AnySlotRebind.HasObjectSlotArity(call.Method.Value, call.Args.OrEmpty().Length)
                ? AnySlotRebind.FromCall(call, consumeAnySlot: false) : call;
        }
    }
}
