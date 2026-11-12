using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Temporary boundary for the unswitched pre-suspend body owner. There is one
// typed cleanup policy shared with final physical admission, not two algorithms.
static class EscapingFinallyLowering
{
    public static void Apply(JsonArray[] parts, JsonNode returnType, ReferenceMetadataIndex references, bool physicalMembers)
    {
        var bodies = parts.Select(part => part.Select(node => BirGraphDom.ReadBirStatement(node)).ToArray()).ToArray();
        TypedEscapingFinally.Apply(bodies.SelectMany(TypedProtectedRegions.Statements).ToArray(),
            TypeNode.Read(returnType), references, physicalMembers);
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index].Clear();
            foreach (var statement in bodies[index]) parts[index].Add(((BirGraphValue)statement).ToDom());
        }
    }
}
