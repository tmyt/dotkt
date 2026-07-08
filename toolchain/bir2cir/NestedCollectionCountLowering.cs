using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// `.size` (the `Count` intrinsic) on a NESTED collection generic — a collection whose own element/value type-arg is
// itself a BCL collection generic — must dispatch through the VARIANCE-IMMUNE non-generic `System.Collections.ICollection`.
//
// WHY: `listOf(...).groupBy { .. }` returns `Map<K, List<T>>`. On CLR the runtime map is a `Dictionary<K, IList<T>>`
// (groupByTo builds `LinkedHashMap<K, MutableList<T>>` = the MUTABLE `IList` value) while the app's STATIC view is
// `IDictionary<K, IReadOnlyList<T>>` (Kotlin's read-only `List`). `Map.size` -> `Count` resolves via the INVARIANT
// `ICollection<KeyValuePair<K,V>>`, so the app dispatches `ICollection<KVP<int,IReadOnlyList<int>>>::get_Count`, a slot
// the runtime `Dictionary<int,IList<int>>` does NOT implement -> EntryPointNotFoundException. The Count is purely an
// element-count read (element-type-independent), so the non-generic `System.Collections.ICollection.Count` — which every
// BCL-backed map/list implements — is the correct, variance-immune target. (Same non-generic escape hatch that
// StarProjectionLowering already uses for `<*>`-erased receivers; here the trigger is a nested-collection type-arg.)
//
// SCOPED so it only fires when a type-arg is itself a `clrg:System.Collections.*` generic — exactly the mutable/read-only
// reification-mismatch case. A flat `Map<Int,Int>` / `List<Int>` keeps its covariant `IReadOnlyCollection.Count` path
// (never a mismatch, since the value type is not a dual-representation collection). App build only.
static class NestedCollectionCountLowering
{
    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "clrPropGet" && Str(o["name"]) == "Count" && o["recv"] is JsonNode recv)
            {
                if (HasNestedCollectionArg(TypeJson.Read(o["type"])))
                {
                    o["type"] = TypeJson.Fqn("System.Collections.ICollection");
                    o["recv"] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Collections.ICollection"), ["e"] = recv.DeepClone() };
                }
                // Star-projected / `Any`-erased receiver: StarProjectionLowering already re-pointed the receiver `cast`
                // at a NON-generic BCL collection interface (`IList`/`IDictionary`/`ICollection`), but the `.size` accessor
                // was bound (by MemberCallSubstitution) to the GENERIC `IReadOnly*<object>.Count`, unimplemented by a
                // value-type-arg collection -> EntryPointNotFound for `List<int>`. Re-point Count at the non-generic
                // `System.Collections.ICollection` (which IList/IDictionary/ICollection all inherit) on the same cast.
                else if (recv is JsonObject rc && Str(rc["k"]) == "cast" && IsNonGenericBclCollection(TypeJson.Read(rc["type"])))
                {
                    o["type"] = TypeJson.Fqn("System.Collections.ICollection");
                    rc["type"] = TypeJson.Fqn("System.Collections.ICollection");
                }
            }
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    // True when the LOWERED type is a constructed BCL collection generic (`System.Collections.*<…>`) at least one of
    // whose top-level type-args is ITSELF a constructed BCL collection generic — the mutable/read-only reification
    // mismatch that must dispatch Count through the non-generic ICollection. (Runs post-lowering, so tokens are the
    // resolved System.Collections Fqn nodes, not the source `clrg:` strings.)
    static bool HasNestedCollectionArg(TypeNode t) =>
        t is TypeNode.Fqn f && f.Args is { Length: > 0 } && IsBclCollection(f)
        && f.Args.Any(a => a is TypeNode.Fqn af && af.Args is { Length: > 0 } && IsBclCollection(af));

    static bool IsBclCollection(TypeNode.Fqn f) =>
        f.Name.StartsWith("System.Collections.", System.StringComparison.Ordinal);

    // A NON-generic BCL collection interface that inherits `System.Collections.ICollection.Count` (the star-projected
    // receiver StarProjectionLowering produced). IEnumerable is excluded — it has no Count.
    static bool IsNonGenericBclCollection(TypeNode t) => t is TypeNode.Fqn { Args: null } f
        && f.Name is "System.Collections.ICollection" or "System.Collections.IList" or "System.Collections.IDictionary";

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}
