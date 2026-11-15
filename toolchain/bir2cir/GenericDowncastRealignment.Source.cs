using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericDowncastRealignment
{
    internal static void Apply(BirFileDeclaration root, IReadOnlyDictionary<string, Def> defs)
    {
        RewriteCasts(root);
        Align(root, ImmutableDictionary.Create<string, TypeNode.Fqn>(StringComparer.Ordinal));

        void RewriteCasts(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            // Preserve this owner's pre-order cast decision: child realignment
            // must not become a new source-type inference for its parent.
            if (node is BirCast { Type.Value: TypeNode.Fqn target } cast
                && SourceExpressionType(cast.E.OrDefault() as BirGraphValue) is TypeNode.Fqn source
                && Realign(target, source, defs) is TypeNode.Fqn aligned)
                cast.Type = aligned;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (!NullableRepresentationViews.Frozen(role)) RewriteCasts(slot.Value);
            });
        }

        void Align(BirGraphValue node, ImmutableDictionary<string, TypeNode.Fqn> locals)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirVariable { Init.Value: BirCast { Type.Value: TypeNode.Fqn type } } variable)
                variable.Type = BirGraphValue.CopyType(type);
            if (node is BirCallInstance { Recv.Value: BirLocal { Name.HasValue: true } local,
                    OwnerType.Value: TypeNode.Fqn owner } call
                && locals.TryGetValue(local.Name.Value, out var actual) && actual.Name == owner.Name)
                call.OwnerType = BirGraphValue.CopyType(actual);

            var childScope = locals;
            node.VisitSourceParameters((name, _) => childScope = childScope.Remove(name));
            node.VisitSourceChildren((_, role, slot) => {
                if (!NullableRepresentationViews.Frozen(role)) Align(slot.Value, childScope);
            }, region => {
                var current = childScope;
                foreach (var statement in region.Statements)
                {
                    Align((BirGraphValue)statement, current);
                    if (statement is BirVariable { Name.HasValue: true } declared)
                    {
                        current = current.Remove(declared.Name.Value);
                        if (declared.Init.OrDefault() is BirCast { Type.Value: TypeNode.Fqn selected })
                            current = current.SetItem(declared.Name.Value, selected);
                    }
                }
                if (region.Result is { } result) Align(result.Value, current);
            });
        }
    }

    // Ret then own type is this pass's existing read policy, not Sty-first
    // static type inference. The old unproduced `stype` spelling has no model.
    static TypeNode SourceExpressionType(BirGraphValue expression)
    {
        TypeNode result = null, own = null;
        expression?.VisitOwnTypeFields((field, type) => {
            if (field == "ret") result = type;
            else if (field == "type") own = type;
        });
        return result ?? own;
    }
}
