using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StarProjectionBoundLowering
{
    internal static void Apply(BirGraphValue root, IReadOnlyDictionary<string, TypeNode[]> localBounds,
        ReferenceMetadataIndex references)
    {
        void Walk(BirGraphValue node)
        {
            if (node is null or IBirExecutablePayload) return;
            // Static containers and declaration identity were excluded by the
            // old owner, not by a general generic-frame-role classification.
            static bool Excluded(string field) => field is "name" or "owner";
            node.RewriteOwnFieldsInPlace((field, _, type) => Excluded(field) ? type : Subst(type, localBounds, references),
                (field, _, slot) => { if (!Excluded(field)) Walk(slot.Value); });
        }
        Walk(root);
    }

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
