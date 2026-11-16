using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionBclSlotSynthesis
{
    internal static void RecordContracts(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var definitions = SupertypeGraph.CollectSource(roots);
        var owners = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
            if (owner.Name.HasValue) owners[owner.Name.Value] = owner;
        foreach (var definition in definitions.Values.Where(definition => definition.Kind == "class"))
        {
            var contracts = SupertypeGraph.Reachable(definition, definitions, refs,
                    sourceFrames: true, physicalSourceApplications: true)
                .Select(edge => new TypeNode.Fqn(ReferenceMetadataIndex.BareOwnerFqn(edge.spec.Name), edge.spec.Args))
                .Where(owner => owner.Name is "kotlin.collections.MutableCollection"
                    or "kotlin.collections.MutableList" or "kotlin.collections.MutableSet")
                .ToArray();
            // Operational obligations are immutable declaration-frame facts,
            // not value slots for Continuation or physical type erasure.
            if (contracts.Length != 0)
                ((BirClassDefinition)owners[definition.Name]).DotktKotlinMutableCollectionContract = SourceTypeVector.Create(contracts);
        }
    }
}
