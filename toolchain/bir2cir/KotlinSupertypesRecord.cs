using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Shared pass-local hand-off for every BIR transform that changes the Kotlin identity of a supertype edge or a
// type-parameter bound. Producers contribute only the positions they move; RoundtripMetadata consumes the merged
// source truth into one [KotlinSupertypes] carrier.
static class KotlinSupertypesRecord
{
    internal const string PreKey = "kotlinSupertypesPre";

    public static void Merge(JsonObject declaration, JsonObject additions)
    {
        if (additions.Count == 0) return;
        var merged = Read(declaration) ?? new JsonObject();

        // There is only one base edge. An earlier producer observed an earlier (and therefore less-erased) form.
        if (merged["base"] == null && additions["base"] is JsonNode addedBase)
            merged["base"] = addedBase.DeepClone();

        MergeInterfaces(merged, additions);
        MergeBounds(merged, additions);
        declaration[PreKey] = merged.ToJsonString();
    }

    static JsonObject Read(JsonObject declaration)
    {
        if ((declaration[PreKey] as JsonValue)?.TryGetValue<string>(out var encoded) != true)
            return null;
        return JsonNode.Parse(encoded) as JsonObject
            ?? throw new InvalidOperationException($"malformed pass-local {PreKey} payload");
    }

    static void MergeInterfaces(JsonObject merged, JsonObject additions)
    {
        if (additions["interfaces"] is not JsonArray added || added.Count == 0) return;
        var target = merged["interfaces"] as JsonArray;
        if (target == null)
        {
            target = new JsonArray();
            merged["interfaces"] = target;
        }
        foreach (var edge in added)
        {
            if (TypeJson.Read(edge) is not TypeNode candidate) continue;
            if (target.Any(existing => SameHead(TypeJson.Read(existing), candidate))) continue;
            target.Add(edge?.DeepClone());
        }
    }

    static void MergeBounds(JsonObject merged, JsonObject additions)
    {
        if (additions["bounds"] is not JsonObject added || added.Count == 0) return;
        var target = merged["bounds"] as JsonObject;
        if (target == null)
        {
            target = new JsonObject();
            merged["bounds"] = target;
        }
        // Each entry is the parameter's whole constraint list. If an earlier transform moved any constraint, that
        // earlier list already preserves every sibling at its least-erased form.
        foreach (var bound in added)
            if (!target.ContainsKey(bound.Key)) target[bound.Key] = bound.Value?.DeepClone();
    }

    static bool SameHead(TypeNode left, TypeNode right) => left is TypeNode.Fqn lf && right is TypeNode.Fqn rf
        && lf.Name == rf.Name && (lf.Args?.Length ?? 0) == (rf.Args?.Length ?? 0);
}
