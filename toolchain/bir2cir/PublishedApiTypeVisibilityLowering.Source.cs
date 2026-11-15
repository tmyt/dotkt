using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PublishedApiTypeVisibilityLowering
{
    // The current owning declaration vocabulary is flat. Run after synthesis
    // and immediately before TypeOwnershipLowering selects CLR nesting.
    internal static void ApplyAll(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
            {
                var visibility = ResolveVisibility(type);
                if (visibility == null) continue;
                if (visibility != type.Vis.OrDefault()) RoundtripMetadata.PreserveTypeVisibility(type);
                type.Vis = visibility;
            }
    }
}
