using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin-owned hash storage uses the same equality/hash contract as Kotlin calls.
// Explicit projected CLR constructors retain their selected native comparer.
static class KotlinHashStorageLowering
{
    const string StorageSignature = "kotlinHashStorageSignature";

    internal static void RequestComparer(JsonObject construction) => construction[StorageSignature] = true;

    // These generated native signatures are applications of the storage owner's frame, not independent source
    // declaration types. Close them only after Kotlin value representation (including existential carriers) settles.
    internal static void Complete(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Complete(child);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToList()) if (child != null) Complete(child);
        if (!obj.Remove(StorageSignature)) return;
        var storage = (TypeNode.Fqn)TypeJson.Read(obj["type"]);
        AddComparer((JsonArray)obj["args"], (JsonArray)obj["argTypes"], storage.Args[0]);
    }

    static void AddComparer(JsonArray args, JsonArray signature, TypeNode key)
    {
        var comparer = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.NativeHashComparer", new[] { key });
        signature.Add(TypeJson.Write(comparer));
        args.Add(new JsonObject {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.KotlinHashComparerKt"),
            ["method"] = "kotlinHashComparer",
            ["typeArgs"] = new JsonArray(TypeJson.Write(key)),
            ["sig"] = new JsonArray(),
            ["ret"] = TypeJson.Write(comparer),
            ["args"] = new JsonArray(),
        });
    }
}
