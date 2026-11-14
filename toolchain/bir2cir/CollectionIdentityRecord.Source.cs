using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionIdentityRecord
{
    internal static void RecordTypeEdges(BirFileDeclaration file)
    {
        foreach (var declaration in file.Types.OrEmpty())
        {
            var baseType = declaration.Base.OrDefault();
            if (baseType == null || !NestsCollapsingReadonly(baseType)) baseType = null;
            var interfaces = declaration.Interfaces.OrEmpty().Where(type => NestsCollapsingReadonly(type)
                || type is TypeNode.Fqn { Name: "kotlin.collections.MutableCollection"
                    or "kotlin.collections.MutableList" or "kotlin.collections.MutableSet" })
                .Select(BirGraphValue.CopyType).ToArray();
            // This record owns only declared rows, not a captured-parameter
            // prefix or the independently frozen nullable companion frame.
            var bounds = new Dictionary<int, TypeNode[]>();
            var parameters = declaration.TypeParams.OrEmpty();
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].Constraints.HasValue && parameters[index].Constraints.Value.Any(NestsCollapsingReadonly))
                    bounds[index] = parameters[index].Constraints.Value.Select(BirGraphValue.CopyType).ToArray();
            var node = (BirGraphValue)declaration;
            var before = NullableRepresentationViews.TypeFacts(node);
            var after = (before ?? SourceTypeFacts.Empty).Merge(BirGraphValue.CopyType(baseType), interfaces, bounds);
            if (!ReferenceEquals(after, before ?? SourceTypeFacts.Empty))
                NullableRepresentationViews.SetTypeFacts(node, after);
        }
    }
}
