using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataIndex
{
    internal static ReferenceMetadataIndex SourceCaptureTestIndex()
    {
        var metadata = new ReferenceDotKtMetadata();
        metadata.TypeParamDeclarations["external.Frame"] = new[] {
            new BirTypeParameter { Name = "O", Variance = "out" },
            new BirTypeParameter { Name = "NO", Variance = "out" },
            new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { new TypeNode.Tv("type", 0) },
                SpecialConstraints = new[] { "class" } },
        };
        metadata.NullableFrames["external.Frame"] = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 0, 2, 1 });
        metadata.TypeParamDeclarations["external.Invariant"] = new[] { new BirTypeParameter { Name = "T" } };
        ReferenceMetadataIndex Create() => new(new List<ReferenceAssembly> {
            new("source-parameters.dll", "source-parameters", "1", metadata),
        }, ManagedReferenceCatalog.Create(Array.Empty<string>(), "bir2cir"));
        var index = Create();
        var first = index.OwnerTypeParametersInPhysicalFrame("external.Frame");
        if (first.Count != 3 || first[0].Variance.Value != "out"
            || first[2].Constraints.Value.Single() != new TypeNode.Tv("type", 0)
            || first[2].SpecialConstraints.Value.Single() != "class"
            || !ReferenceEquals(first, index.OwnerTypeParametersInPhysicalFrame("external.Frame"))
            || ReferenceEquals(first, Create().OwnerTypeParametersInPhysicalFrame("external.Frame"))
            || index.OwnerTypeParametersInPhysicalFrame("external.Missing") != null)
            throw new InvalidOperationException("Typed reference parameters lost facts or index-local cache ownership");
        var selected = index.OwnerTypeParamDeclarations("external.Frame");
        selected[2].Constraints.Value[0] = new TypeNode.Fqn("changed");
        selected[0].Name = "changed";
        if (first[0].Name.Value != "O" || first[2].Constraints.Value[0] != new TypeNode.Tv("type", 0)
            || index.OwnerTypeParamDeclarations("external.Invariant")[0].Constraints.IsPresent)
            throw new InvalidOperationException("Selected reference parameter results alias the read-only catalog or invent absent bounds");
        return index;
    }
}
