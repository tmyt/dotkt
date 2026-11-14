using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A CLR generic argument cannot distinguish `String` from `String?` (or `Int` from `Int?` after the Kotlin
// instantiation has selected the same open `!!T` body). Collection storage also merges read-only/mutable faces.
// Preserve nullability, nominal flags and the runtime classifier as one explicit ABI witness for each METHOD
// type parameter whose body transitively performs a type-sensitive operation. Kotlin
// `reified` is retained separately as declaration metadata; it never selects this physical ABI. This layer threads
// structurally-derived demand through exact declaration identities and explicit lifted-frame correspondences, while
// ilemit merely emits the resulting CIR expression.
static partial class ReifiedNullabilityWitnessLowering
{
    internal const string SemanticIndicesKey = "semanticReifiedIndices";
    internal const string WitnessIndicesKey = "nullableWitnessIndices";
    const string CallWitnessCountKey = "nullableWitnessCount";
    const string PendingWitnessesKey = "nullablePendingWitnesses";
    const string Prefix = "dotkt$nullableWitness$";
    public static void MaterializeCallWitnesses(JsonNode root)
    {
        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj.ToList())
                        if (property.Key != PendingWitnessesKey && property.Value != null) Walk(property.Value);
                    if (obj[PendingWitnessesKey] is not JsonArray pending) return;
                    if (Str(obj["k"]) is not ("callStatic" or "callInstance" or "constrainedCall" or "new"))
                        throw new InvalidOperationException(
                            $"bir2cir: nullable witness survived on non-call node '{Str(obj["k"])}'");
                    var args = obj["args"] as JsonArray
                        ?? throw new InvalidOperationException("bir2cir: nullable-sensitive call lost its argument vector");
                    foreach (var witness in pending)
                        args.Add(witness?.DeepClone());
                    obj.Remove(PendingWitnessesKey);
                    obj[CallWitnessCountKey] = pending.Count;
                    FinalizeVector(obj, "sig", args.Count, pending.Count);
                    FinalizeVector(obj, "argTypes", args.Count, pending.Count);
                    FinalizeVector(obj, "memberSignature", args.Count, pending.Count);
                    break;
                case JsonArray array:
                    foreach (var child in array.ToList()) if (child != null) Walk(child);
                    break;
            }
        }

        Walk(root);
    }

    public static void FinalizeCallSignatures(JsonNode root)
    {
        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var child in obj.Select(property => property.Value).ToList())
                        if (child != null) Walk(child);
                    if (obj[CallWitnessCountKey] is not JsonValue countValue
                        || !countValue.TryGetValue<int>(out var count) || count <= 0)
                        return;
                    var argumentCount = (obj["args"] as JsonArray)?.Count
                        ?? throw new InvalidOperationException("bir2cir: nullable-sensitive call lost its argument vector");
                    FinalizeVector(obj, "sig", argumentCount, count);
                    FinalizeVector(obj, "argTypes", argumentCount, count);
                    FinalizeVector(obj, "memberSignature", argumentCount, count);
                    obj.Remove(CallWitnessCountKey);
                    break;
                case JsonArray array:
                    foreach (var child in array.ToList()) if (child != null) Walk(child);
                    break;
            }
        }
        Walk(root);
    }

    static void FinalizeVector(JsonObject call, string key, int argumentCount, int witnessCount)
    {
        if (call[key] is not JsonArray vector) return;
        var missing = MissingWitnessTypes(key, vector.Count, argumentCount, witnessCount);
        for (var index = 0; index < missing; index++) AppendWitnessType(call, key);
    }

    // Early owning materialization and the late signature repair have the same
    // vector contract; neither may silently supply a missing Kotlin argument.
    static int MissingWitnessTypes(string key, int vectorCount, int argumentCount, int witnessCount)
    {
        if (vectorCount > argumentCount || argumentCount - vectorCount > witnessCount)
            throw new InvalidOperationException(
                $"bir2cir: nullable-sensitive call has inconsistent '{key}' ({vectorCount}) and argument ({argumentCount}) counts");
        return argumentCount - vectorCount;
    }

    static void AppendWitnessType(JsonObject call, string key)
    {
        if (call[key] is JsonArray vector) vector.Add(KotlinRuntimeTypeWitness.TypeJsonNode());
    }

    static string Sanitize(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
