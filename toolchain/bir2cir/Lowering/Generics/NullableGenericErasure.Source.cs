using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableGenericErasure
{
    // Continue the actual owning declarations. The source carriers must be
    // captured before either the loop element or a declaration slot is erased.
    internal static void Apply(BirFileDeclaration root, ValueTypeOracle isValue)
    {
        PreserveSourceFacts(root, isValue);
        foreach (var owner in NullableRepresentationViews.Owners(new[] { root }))
            foreach (var method in owner.Methods.OrEmpty())
                EraseSourceLoops(method);
        EraseSourceTypes(root, isValue);
    }

    static void EraseSourceLoops(BirFunctionDeclaration method)
    {
        var sources = new Dictionary<string, TypeNode.Tv>(StringComparer.Ordinal);
        foreach (var parameter in method.Params.OrEmpty())
            if (parameter.Name.HasValue && parameter.Type.OrDefault() is { } type
                && ExtractNullableTv(type) is { } element)
                sources[parameter.Name.Value] = element;
        if (sources.Count == 0) return;
        foreach (var statement in method.Body.OrEmpty()) ErodeSourceForEach((BirGraphValue)statement, sources);
    }

    static void ErodeSourceForEach(BirGraphValue node, IReadOnlyDictionary<string, TypeNode.Tv> sources)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirForEach { Src.HasValue: true, Elem.HasValue: true, Var.HasValue: true } loop
            && loop.Src.Value is BirLocal { Name.HasValue: true } source
            && sources.TryGetValue(source.Name.Value, out var sourceElement)
            && (loop.Elem.Value is TypeNode.Nullable { Of: TypeNode.Tv } nullable
                ? nullable.Of : loop.Elem.Value) is TypeNode.Tv element && element == sourceElement)
        {
            loop.Elem = new TypeNode.Fqn("object");
            foreach (var statement in loop.Body.OrEmpty())
                RenarrowSourceLoopArguments((BirGraphValue)statement, loop.Var.Value, element);
        }
        VisitErasureChildren(node, child => ErodeSourceForEach(child, sources));
    }

    static void VisitErasureChildren(BirGraphValue node, Action<BirGraphValue> visit)
        => node.VisitGenericFrame((_, _) => { }, (ownership, child) => {
            if (!NullableRepresentationViews.Frozen(ownership)
                && ownership != BirGenericFrameRole.OverrideDeclaration && child is not IBirExecutablePayload)
                visit(child);
        });

    static void RenarrowSourceLoopArguments(BirGraphValue node, string name, TypeNode.Tv element)
    {
        if (node is IBirExecutablePayload) return;
        node.VisitOwnChildSlots((field, ownership, slot) => {
            if (NullableRepresentationViews.Frozen(ownership)
                || ownership == BirGenericFrameRole.OverrideDeclaration) return;
            if (field == "args" && slot.Value is BirLocal { Name.HasValue: true } local && local.Name.Value == name)
                slot.Replace(new BirCast { Type = element, E = OptionalField<IBirExpression>.Of((IBirExpression)local.DeepClone()) });
            RenarrowSourceLoopArguments(slot.Value, name, element);
        });
    }

    static void EraseSourceTypes(BirGraphValue node, ValueTypeOracle isValue, Pos position = Pos.Slot)
    {
        if (node is IBirExecutablePayload) return;
        var kind = (node as BirGraphNode)?.Tag;
        var changedResult = false;
        node.RewriteOwnTypeFieldsInPlace((field, ownership, type) => {
            if (NullableRepresentationViews.Frozen(ownership)
                || ownership == BirGenericFrameRole.OverrideDeclaration) return type;
            var erased = Erase(type, PositionForField(field, kind, position), isValue);
            if (field is "ret" or "dynRet" && !erased.Equals(type)) changedResult = true;
            return erased;
        });
        node.VisitOwnChildSlots((field, ownership, slot) => {
            if (!NullableRepresentationViews.Frozen(ownership)
                && ownership != BirGenericFrameRole.OverrideDeclaration)
                EraseSourceTypes(slot.Value, isValue, PositionForField(field, kind, position));
        });
        if (!changedResult) return;
        // Ret/dynRet are declaration shapes, not necessarily this occurrence's
        // instantiated result. Consume only a stamp refuted by the shared
        // sanity relation, just as NodeType.DropStampIfStale does on late DOM.
        TypeNode stamp = null;
        node.VisitOwnTypeFields((field, type) => { if (field == "sty") stamp = type; });
        if (stamp == null) return;
        var stale = false;
        node.VisitOwnTypeFields((field, type) => {
            if (field is "ret" or "dynRet" && !IrSanity.StampAgrees(stamp, type)) stale = true;
        });
        if (stale) node.ClearResultTypeStamp();
    }
}
