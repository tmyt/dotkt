using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class ValueElementIterableCoercion
{
    internal static void Apply(BirFileDeclaration root, ValueTypeOracle isValue,
        Dictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs)
    {
        isValue ??= _ => false;
        // Refresh this file's declarations: synthesis may add classes after
        // the original module snapshot was captured.
        foreach (var entry in SupertypeGraph.CollectSource(new[] { root })) definitions[entry.Key] = entry.Value;
        Walk(root);

        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirCallStatic call)
            {
                // Explicit null and absence both fall through to the selected shape, as in the DOM owner.
                var signature = call.Sig.OrDefault() ?? call.ShapeTypes.OrDefault();
                if (signature != null && call.Args.HasValue && signature.Length == call.Args.Value.Length)
                    for (var index = 0; index < signature.Length; index++)
                    {
                        var argument = call.Args.Value[index];
                        if (BirNodeType.Of(argument as BirGraphNode) is not TypeNode.Fqn source) continue;
                        var target = CastTarget(signature[index], call.TypeArgs.OrDefault(), source, definitions, refs, isValue);
                        if (target == null || argument is BirGenericStatic { Method.HasValue: true, Method.Value: "Cast" }) continue;
                        call.Args.Value[index] = CastElements(argument, target);
                    }
            }
            // Preserve the original pre-order: newly selected Cast nodes are traversed once, too.
            node.VisitChildren(Walk);
        }
    }

    internal static BirGenericStatic CastElements(IBirExpression source, TypeNode target) => new() {
        Type = new TypeNode.Fqn("System.Linq.Enumerable"), Method = "Cast",
        TypeArgs = new[] { target },
        ResolvedMemberParams = new TypeNode[] { new TypeNode.Fqn("System.Collections.IEnumerable") },
        Args = new[] { source }, Sty = CastResultType(target),
    };
}
