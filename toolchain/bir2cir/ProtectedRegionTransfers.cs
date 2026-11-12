using System.Text.Json.Nodes;
using DotKt.Bir.Lowering;

// Temporary neighbour adapter while physical coercion/metadata still own DOM.
// The control-flow policy itself exists only over owning typed occurrences.
// Delete this adapter when the surrounding physical tail stays typed through CIR.
static class ProtectedRegionTransfers
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex references)
    {
        var graph = BirGraphDom.ReadBirFileDeclaration(root);
        TypedProtectedRegions.Apply(graph, references);
        var result = graph.ToDom();
        var entries = result.ToArray();
        result.Clear();
        root.AsObject().Clear();
        foreach (var entry in entries) root.AsObject().Add(entry.Key, entry.Value);
    }
}
