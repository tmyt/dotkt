using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Shared pass-local hand-off for every BIR transform that changes the Kotlin identity of a supertype edge or a
// type-parameter declaration fact. Producers contribute only the positions they move; RoundtripMetadata consumes the
// merged source truth into one [KotlinSupertypes] carrier.
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
        MergeVariances(merged, additions);
        if (additions[NullableRepresentationFrame.MetadataKey] is JsonNode frameNode)
        {
            var frame = NullableRepresentationFrame.Read(frameNode).ToJson();
            if (merged[NullableRepresentationFrame.MetadataKey] is JsonNode prior
                && !JsonNode.DeepEquals(prior, frame))
                throw new InvalidOperationException("Conflicting declaration-owned nullable representation frames");
            merged[NullableRepresentationFrame.MetadataKey] = frame;
        }
        declaration[PreKey] = merged.ToJsonString();
    }

    internal static NullableRepresentationFrame ReadNullableFrame(JsonObject declaration) =>
        Read(declaration)?[NullableRepresentationFrame.MetadataKey] is JsonNode frame
            ? NullableRepresentationFrame.Read(frame) : null;

    // Owner capture changes a synthesized declaration's source and physical
    // frames together. Rebase its recorded source facts through that exact map;
    // this is not an erasure or reconstruction of a missing frame.
    internal static void RebaseCapturedFrame(JsonObject declaration, NullableRepresentationFrame frame,
        int[] sourceMap, JsonObject owner)
    {
        var facts = Read(declaration) ?? new JsonObject();
        void RemapTypes(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (TypeJson.Read(obj) is TypeNode.Tv { Scope: "type" } variable)
                {
                    obj["i"] = sourceMap[variable.I];
                    return;
                }
                foreach (var (key, child) in obj)
                    if (key != NullableRepresentationFrame.MetadataKey) RemapTypes(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) RemapTypes(child);
        }
        RemapTypes(facts);
        foreach (var key in new[] { "bounds", "variances" })
            if (facts[key] is JsonObject indexed)
                facts[key] = new JsonObject(indexed.Select(pair => KeyValuePair.Create(
                    sourceMap[int.Parse(pair.Key)].ToString(), pair.Value?.DeepClone())));
        if (Read(owner)?["bounds"] is JsonObject ownerBounds)
        {
            var bounds = facts["bounds"] as JsonObject ?? new JsonObject();
            foreach (var (key, value) in ownerBounds) bounds[key] = value?.DeepClone();
            if (facts["bounds"] == null) facts["bounds"] = bounds;
        }
        facts[NullableRepresentationFrame.MetadataKey] = frame.ToJson();
        declaration[PreKey] = facts.ToJsonString();
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

    static void MergeVariances(JsonObject merged, JsonObject additions)
    {
        if (additions["variances"] is not JsonObject added || added.Count == 0) return;
        var target = merged["variances"] as JsonObject;
        if (target == null)
        {
            target = new JsonObject();
            merged["variances"] = target;
        }
        foreach (var variance in added)
            if (!target.ContainsKey(variance.Key)) target[variance.Key] = variance.Value?.DeepClone();
    }

    static bool SameHead(TypeNode left, TypeNode right) => left is TypeNode.Fqn lf && right is TypeNode.Fqn rf
        && lf.Name == rf.Name && (lf.Args?.Length ?? 0) == (rf.Args?.Length ?? 0);
}
