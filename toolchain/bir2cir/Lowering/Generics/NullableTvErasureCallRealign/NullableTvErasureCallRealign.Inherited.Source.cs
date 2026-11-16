using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    // The same canonical declaration index already carries physical owner
    // applications. Inherited binding changed call targets, not eligibility.
    internal static void ApplyAfterInheritedOwnerBinding(BirFileDeclaration root,
        DeclIndex index, ValueTypeOracle isValue, ReferenceMetadataIndex references)
    {
        _isValue = isValue ?? (_ => false);
        _refs = references;
        _sourceApplicationOrder = null;
        foreach (var method in root.Methods.OrEmpty()) SourceUseMethod(method, index, null);
        foreach (var owner in root.Types.OrEmpty()) SourceUseOwner(owner, index);
    }

    // The selected object seam is shared with the genuine late DOM buyer.
    // Wrapping moves the actual operand; it does not duplicate an executable.
    internal static IBirExpression CastForErasedObjectSlot(IBirExpression value,
        TypeNode source, TypeNode target, ValueTypeOracle isValue)
    {
        if (value is not BirGraphNode node) return null;
        var selected = SelectObjectSlotCoercion(node.Tag,
            node is BirCast cast ? cast.Type.OrDefault() : null,
            source, target, exactPropertyTarget: false, isValue);
        return selected.Kind == SlotCoercionKind.Cast
            ? new BirCast { Type = BirGraphValue.CopyType(selected.Type), E = OptionalField<IBirExpression>.Of(value) }
            : null;
    }
}
