using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class AnySlotRebind
{
    public static BirFileDeclaration Apply(BirFileDeclaration root, Dictionary<string, TypeInfo> types,
        ReferenceMetadataIndex references)
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
            if (node is not BirCallInstance call || !call.Method.HasValue || !IsObjectSlot(call.Method.Value)) return node;
            var superCall = call.Super.OrDefault();
            if (superCall && !call.AnySlot.OrDefault()) return call;
            var owner = (call.OwnerType.OrDefault() as TypeNode.Fqn)?.Name;
            if (IsVirtualToString(call))
            {
                if (owner != null && !types.ContainsKey(owner)
                    && references.TryExistentialPhysicalOwner(owner, out _) && call.Recv.HasValue)
                    return FromCall(call, consumeAnySlot: true);
                if (!AliasHelperHoist.UsesNativeArrayObjectSlot(owner, true)) call.AnySlot = default;
            }
            if (owner == null || !types.ContainsKey(owner) || ResolvesSlot(types, owner, call.Method.Value)) return call;
            if (superCall)
            {
                call.OwnerType = new TypeNode.Fqn("kotlin.Any");
                return call;
            }
            return call.Recv.HasValue && HasObjectSlotArity(call.Method.Value, call.Args.OrEmpty().Length)
                ? FromCall(call, consumeAnySlot: true) : call;
        }
    }

    // Call classification owns whether the retained Any-slot intent survives.
    // A shape change transfers each actual operand once, retaining only the
    // node-local result fact and the explicitly selected semantic marker.
    internal static BirObjectMethod FromCall(BirCallInstance call, bool consumeAnySlot)
        => new() {
            Method = call.Method,
            Recv = call.Recv,
            Arg = call.Method.OrDefault() == "Equals" && call.Args.HasValue && call.Args.Value.Length > 0
                ? OptionalField<IBirExpression>.Of(call.Args.Value[0]) : default,
            Sty = call.Sty,
            AnySlot = consumeAnySlot ? default : call.AnySlot,
        };
}
