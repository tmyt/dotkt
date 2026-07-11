using System.Text.Json.Nodes;
using DotKt.Bir;

// `.size` (the `Count` intrinsic) on a STAR-PROJECTED / `Any`-erased collection receiver must dispatch through the
// variance-immune non-generic `System.Collections.ICollection`.
//
// WHY: StarProjectionLowering (Phase 1) already re-pointed a `<*>`-erased receiver `cast` at a NON-generic BCL
// collection interface (`IList`/`IDictionary`/`ICollection`), but the `.size` accessor was bound (by
// MemberCallSubstitution) to the GENERIC `IReadOnly*<object>.Count`, which a value-type-arg collection such as
// `List<int>` does not implement -> EntryPointNotFoundException. Re-point the Count accessor at the non-generic
// `System.Collections.ICollection.Count` (which IList/IDictionary/ICollection all inherit) on that same cast.
//
// App build only. (The former nested-collection paper-over branch — for a `Map<K, List<V>>` whose value slot lowered
// to the read-only sibling — was retired once the #75 arg-position variance collapse made the value slot a mutable
// `IList<V>`, so `it.value.size` now routes through the generic `get_Count` on the concrete Dictionary directly.)
static class StarProjectionCountLowering
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            // Star-projected / `Any`-erased receiver: StarProjectionLowering already re-pointed the receiver `cast`
            // at a NON-generic BCL collection interface (`IList`/`IDictionary`/`ICollection`), but the `.size` accessor
            // was bound (by MemberCallSubstitution) to the GENERIC `IReadOnly*<object>.Count`, unimplemented by a
            // value-type-arg collection -> EntryPointNotFound for `List<int>`. Re-point Count at the non-generic
            // `System.Collections.ICollection` (which IList/IDictionary/ICollection all inherit) on the same cast.
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

    // A NON-generic BCL collection interface that inherits `System.Collections.ICollection.Count` (the star-projected
    // receiver StarProjectionLowering produced). IEnumerable is excluded — it has no Count.
    static bool IsNonGenericBclCollection(TypeNode t) => t is TypeNode.Fqn { Args: null } f
        && f.Name is "System.Collections.ICollection" or "System.Collections.IList" or "System.Collections.IDictionary";

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
