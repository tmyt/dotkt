using DotKt.Bir;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex SourceCaptureTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.TypeParamDeclarations["external.Frame"] = """
            [{"name":"O","variance":"out"},{"name":"NO","variance":"out"},
             {"name":"T","constraints":[{"t":"tv","scope":"type","i":0}],"specialConstraints":["class"]}]
            """;
        metadata.NullableFrames["external.Frame"] = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 0, 2, 1 });
        metadata.TypeParamDeclarations["external.Invariant"] = """[{"name":"T"}]""";
        ReferenceMetadataIndex Create() => new(new List<ReferenceAssembly> {
            new("source-parameters.dll", "source-parameters", "1", metadata),
        }, ManagedReferenceCatalog.Create(Array.Empty<string>(), "bir2cir"));
        var index = Create();
        var first = index.SourceOwnerTypeParameters("external.Frame");
        if (first.Count != 3 || first[0].Variance.Value != "out"
            || first[2].Constraints.Value.Single() != new TypeNode.Tv("type", 0)
            || first[2].SpecialConstraints.Value.Single() != "class"
            || !ReferenceEquals(first, index.SourceOwnerTypeParameters("external.Frame"))
            || ReferenceEquals(first, Create().SourceOwnerTypeParameters("external.Frame"))
            || index.SourceOwnerTypeParameters("external.Missing") != null)
            throw new InvalidOperationException("Typed reference parameters lost facts or index-local cache ownership");
        return index;
    }
}
