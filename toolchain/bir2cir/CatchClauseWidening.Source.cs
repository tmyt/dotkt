using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class CatchClauseWidening
{
    internal static void Apply(BirFileDeclaration root) => WidenSource(root);

    static void WidenSource(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return;
        if (node is BirTry { Catches.HasValue: true } statement)
        {
            var widened = new List<BirCatch>();
            var changed = false;
            foreach (var clause in statement.Catches.Value)
                if (clause.ExcType.OrDefault() is TypeNode.Fqn exception
                    && ReferenceMetadataIndex.BareOwnerFqn(exception.Name) == "kotlin.IndexOutOfBoundsException")
                {
                    changed = true;
                    foreach (var target in IndexOobNet)
                    {
                        var copy = (BirCatch)clause.DeepClone();
                        copy.ExcType = new TypeNode.Fqn(target);
                        widened.Add(copy);
                    }
                }
                else widened.Add(clause);
            if (changed) statement.Catches = widened.ToArray();
        }
        // Widen an outer clause before visiting its independent copies. Nested
        // catches therefore receive the same expansion without shared bodies.
        node.VisitChildren(WidenSource);
    }
}
