using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ConstructedMemberReturnSubstitution
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        foreach (var root in roots) ApplySourceMaterialized(root);
    }

    internal static void ApplySourceMaterialized(BirGraphValue root)
    {
        if (root is IBirExecutablePayload) return;
        ApplySourceCall(root);
        root.VisitChildren(ApplySourceMaterialized);
    }

    static void ApplySourceCall(BirGraphValue node)
    {
        var ownerField = node switch {
            BirCallInstance => "ownerType",
            BirNativeInstance or BirGenericInstance or BirPropertyGet => "type",
            _ => null,
        };
        if (ownerField == null) return;
        TypeNode owner = null, stamp = null;
        node.VisitOwnTypeFields((field, type) => {
            if (field == ownerField) owner = type;
            if (field == "sty") stamp = type;
        });
        if (owner is not TypeNode.Fqn { Args: { } arguments }) return;
        var changed = false;
        node.RewriteOwnTypeFieldsInPlace((field, _, type) => {
            if (field is not ("ret" or "dynRet") || !TryCloseResult(type, stamp, arguments, out var closed)) return type;
            changed = true;
            return closed;
        });
        if (!changed || stamp == null) return;
        var stale = false;
        node.VisitOwnTypeFields((field, type) => {
            if (field is "ret" or "dynRet" && !IrSanity.StampAgrees(stamp, type)) stale = true;
        });
        if (stale) node.ClearResultTypeStamp();
    }
}
