using System.Text.Json.Nodes;

// Shared heap cells and the CharSequence representation interface are authored
// below Kotlin source projection, from explicit registry/use facts. The owning
// typed entry runs after closure synthesis and appends only new declarations.
static partial class SharedSyntheticSynthesis
{
    public const string CharSeq = "dotkt$CharSequence";

    // Scalar handoff to later physical storage/erasure owners.
    internal readonly record struct CellStorage(string Owner, string Field, int ConstructorParameter);

    // Remaining late DOM materializers consume these transient source facts.
    // Retire this adapter with those actual owners, not before their callers.
    public static void DropSyntheticTypeArgs(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("_syntheticTypeArgs");
                obj.Remove("sharedCellType");
                obj.Remove("sharedCellTypeParams");
                foreach (var child in obj) if (child.Value != null) DropSyntheticTypeArgs(child.Value);
                break;
            case JsonArray array:
                foreach (var child in array) if (child != null) DropSyntheticTypeArgs(child);
                break;
        }
    }
}
