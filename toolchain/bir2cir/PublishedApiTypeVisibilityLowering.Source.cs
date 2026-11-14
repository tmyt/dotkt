using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class PublishedApiTypeVisibilityLowering
{
    // The current owning declaration vocabulary is flat. Run after synthesis
    // and immediately before TypeOwnershipLowering selects CLR nesting.
    internal static void ApplyAll(IEnumerable<BirFileDeclaration> roots)
    {
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
            {
                var current = type.Vis.OrDefault();
                var visibility = ResolveVisibility(current, current == "internal" && type.Attrs.OrEmpty()
                    .Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: Marker }));
                if (visibility == null) continue;
                if (visibility != type.Vis.OrDefault()) RoundtripMetadata.PreserveTypeVisibility(type);
                type.Vis = visibility;
            }
    }
}
