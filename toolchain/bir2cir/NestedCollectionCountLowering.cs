using System.Text.Json.Nodes;

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
            if (Str(o["k"]) == "clrPropGet" && Str(o["name"]) == "Count"
                && o["recv"] is JsonNode recv && Str(o["type"]) is string type && HasNestedCollectionArg(type))
            {
                o["type"] = "System.Collections.ICollection";
                o["recv"] = new JsonObject { ["k"] = "cast", ["type"] = "clr:System.Collections.ICollection", ["e"] = recv.DeepClone() };
            }
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    // True when `token` is a constructed BCL collection generic (`clrg:System.Collections.Generic.X[..]`) at least one of
    // whose top-level type-args is ITSELF a constructed BCL collection generic (`clrg:System.Collections.*[..]`).
    static bool HasNestedCollectionArg(string token)
    {
        if (token is null || !token.StartsWith("clrg:System.Collections.", System.StringComparison.Ordinal)) return false;
        var lb = token.IndexOf('[');
        if (lb < 0 || !token.EndsWith("]", System.StringComparison.Ordinal)) return false;
        foreach (var arg in SplitTop(token[(lb + 1)..^1]))
            if (arg.StartsWith("clrg:System.Collections.", System.StringComparison.Ordinal) && arg.Contains('['))
                return true;
        return false;
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;

    // Top-level comma split respecting `[...]` nesting.
    static System.Collections.Generic.List<string> SplitTop(string value)
    {
        var result = new System.Collections.Generic.List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0) { result.Add(value[start..i].Trim()); start = i + 1; }
        }
        if (value.Length > 0) result.Add(value[start..].Trim());
        return result;
    }
}
