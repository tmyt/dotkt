using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExternalGenericConstraintValidation
{
    internal static Prepared Prepare(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums) =>
        new(refs, isValueFqn, localEnums, CollectLocalTypes(roots));

    static Dictionary<string, LocalTypeFacts> CollectLocalTypes(IReadOnlyList<BirFileDeclaration> roots)
    {
        var localTypes = new Dictionary<string, LocalTypeFacts>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            if (!owner.Name.HasValue || !owner.Kind.HasValue || !IsTypeKind(owner.Kind.Value)) continue;
            var kind = owner.Kind.Value;
            var abstractType = owner is BirClassDefinition type && type.Abstract.OrDefault();
            var publicDefault = kind is "struct" or "enum" or "value" || !abstractType &&
                owner.Ctors.OrEmpty().Any(ctor => ctor.Vis.OrDefault() is null or "public"
                    && ctor.Params.HasValue && ctor.Params.Value.Length == 0);
            localTypes[owner.Name.Value] = new(kind, abstractType, publicDefault);
        }
        return localTypes;
    }
}
