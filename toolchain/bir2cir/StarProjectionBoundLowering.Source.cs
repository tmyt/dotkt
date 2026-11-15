using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StarProjectionBoundLowering
{
    // Snapshot the complete captured + declared frame before ABI erasure.
    internal static Dictionary<string, TypeNode[]> CollectTypeParamBounds(IEnumerable<BirFileDeclaration> roots)
    {
        var result = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            if (!owner.Name.HasValue) continue;
            var bounds = TypeParameterFrame.Declarations(owner)
                .Select(parameter => parameter.Constraints.OrEmpty().FirstOrDefault(type => !IsObjectish(type)))
                .Select(BirGraphValue.CopyType).ToArray();
            if (bounds.Any(bound => bound != null)) result.TryAdd(owner.Name.Value, bounds);
        }
        return result;
    }
}
