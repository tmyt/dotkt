using DotKt.Bir;
using DotKt.Bir.Lowering;

// Capture once before semantic helper producers rewrite expressions. This is a
// module-fact handoff, not a second graph or a postponed DOM index over new code.
sealed partial class SourceCollectionInputs
{
    internal SourceHelperBinder Helpers { get; }
    internal InnerApplicationOrder Applications { get; }
    internal IReadOnlyDictionary<string, TypeNode.Fqn> ClosedViews { get; }
    internal IReadOnlySet<string> DeclaredIterators { get; }
    internal IReadOnlyDictionary<string, SupertypeGraph.Def> Definitions { get; }
    internal IReadOnlyDictionary<string, string> Kinds { get; }

    internal SourceCollectionInputs(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex references)
    {
        Helpers = new(roots, references);
        Applications = new(roots, references);
        ClosedViews = references == null ? new Dictionary<string, TypeNode.Fqn>()
            : ForeignStarProjectionBinding.CollectClosedViewHints(roots, references);
        DeclaredIterators = roots.SelectMany(MemberCallSubstitution.CollectDeclaredIteratorTypes).ToHashSet(StringComparer.Ordinal);
        Definitions = SupertypeGraph.CollectSource(roots);
        Kinds = Definitions.ToDictionary(pair => pair.Key, pair => pair.Value.Kind, StringComparer.Ordinal);
    }
}
