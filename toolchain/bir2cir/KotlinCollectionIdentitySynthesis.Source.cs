using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinCollectionIdentitySynthesis
{
    internal static void ApplyAll(IEnumerable<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        // Membership and original edges are captured once. The shared BFS must
        // not observe interfaces appended while applying an earlier owner.
        var definitions = new Dictionary<string, Def<IBirTypeDeclaration>>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
            if (owner.Name.HasValue)
                definitions[owner.Name.Value] = new(owner, owner.Base.OrDefault() as TypeNode.Fqn,
                    owner.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().ToArray());
        foreach (var definition in definitions.Values)
        {
            var selected = SelectIdentities(SupertypeNames(definition, definitions, refs));
            if (selected.Count == 0) continue;
            var interfaces = definition.Node.Interfaces.OrEmpty().ToList();
            foreach (var identity in selected)
                if (!interfaces.OfType<TypeNode.Fqn>().Any(type => type.Name == identity))
                    interfaces.Add(new TypeNode.Fqn(identity));
            if (interfaces.Count != definition.Node.Interfaces.OrEmpty().Length)
                definition.Node.Interfaces = interfaces.ToArray();
        }
    }
}
