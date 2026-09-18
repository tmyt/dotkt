using System.Text.Json.Nodes;
using DotKt.Bir;

// Count on a physical raw collection cast binds to System.Collections.ICollection.Count, not a generic
// IReadOnlyCollection<object> slot. StarProjectionLowering still produces raw IDictionary for Map; Collection
// and List families instead use their composite classifier/member paths. Explicit raw BCL casts can also occur.
// App build only.
static class StarProjectionCountLowering
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            // Preserve the actual non-generic receiver and bind its inherited Count declaration.
            if (Str(o["k"]) == "clrPropGet" && Str(o["name"]) == "Count" && o["recv"] is JsonObject rc
                && Str(rc["k"]) == "cast" && IsNonGenericBclCollection(TypeJson.Read(rc["type"])))
            {
                o["type"] = TypeJson.Fqn("System.Collections.ICollection");
                rc["type"] = TypeJson.Fqn("System.Collections.ICollection");
            }
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    // Raw BCL interfaces that inherit ICollection.Count. IEnumerable has no Count.
    static bool IsNonGenericBclCollection(TypeNode t) => t is TypeNode.Fqn { Args: null } f
        && f.Name is "System.Collections.ICollection" or "System.Collections.IList" or "System.Collections.IDictionary";

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
