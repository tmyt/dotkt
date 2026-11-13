using System.Text.Json.Nodes;
using DotKt.Bir;

// Opaque executable BIR crosses an ownership boundary in KotlinInline/KotlinDefault carriers. The producer stores
// frontend vocabulary intentionally; the consuming bir2cir instance must put that payload through the same
// representation-entry contract as source BIR before any payload-specific rewrite inspects or clones it.
//
// Keep this chokepoint small and ordered. A new representation-entry invariant belongs here when it must hold for
// both inline and default payloads; callers must not repair a materialized payload later with an idempotent file-wide
// sweep. Normal source BIR takes the same first step before Program's source helper recognition.
static partial class MaterializedBirPayload
{
    // Before storing executable BIR in a DLL, turn producer-owned source positions into hidden boundaries.
    // The local compilation retains its own graph and emits visible positions to its separate PDB.
    public static void HideSourceSpans(JsonNode payload)
    {
        if (payload is JsonObject obj)
        {
            if (obj["k"] is JsonValue kind && kind.TryGetValue<string>(out var text) && text == "sourcePoint")
                obj.Remove("span");
            HideDefaultSourceSpans(obj);
            foreach (var pair in obj) HideSourceSpans(pair.Value);
        }
        else if (payload is JsonArray array)
            foreach (var item in array) HideSourceSpans(item);
    }

    // Defaults nested on inline parameters use the same versioned byte-array carrier.
    public static void HideDefaultSourceSpans(JsonObject obj)
    {
        if (DefaultBirCarrier.IsCarrier(obj)) DefaultBirCarrier.Rewrite(obj, HideSourceSpans);
    }

    public static void Normalize(JsonNode payload)
    {
        if (payload == null) return;
        ObjectSlotRename.Apply(payload);
    }

    public static void SelfTest()
    {
        JsonObject AnyCall(string name, bool superCall = false) => new()
        {
            ["k"] = "callInstance", ["method"] = name, ["anySlot"] = true, ["super"] = superCall,
            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "value" },
            ["args"] = name == "equals"
                ? new JsonArray(new JsonObject { ["k"] = "local", ["name"] = "other" }) : new JsonArray(),
        };
        var payload = new JsonObject
        {
            ["body"] = new JsonArray
            {
                AnyCall("toString"), AnyCall("equals"), AnyCall("hashCode"), AnyCall("equals", superCall: true),
            },
            ["lifted"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "hashCode",
                    ["objectOverride"] = true,
                },
            },
        };

        Normalize(payload);
        var call = payload["body"]?[0] as JsonObject;
        var lifted = payload["lifted"]?[0] as JsonObject;
        bool IsEqualityHelper(int index, string method)
        {
            var result = payload["body"]?[index];
            return result?["k"]?.GetValue<string>() == "callStatic"
                && TypeJson.OwnerName(result["owner"]) == "DotKt.Runtime.CompilerServices.KotlinEqualityKt"
                && result["method"]?.GetValue<string>() == method && result["anySlot"] == null;
        }
        var superCall = payload["body"]?[3];
        if (call?["method"]?.GetValue<string>() != "ToString"
            || call["anySlot"]?.GetValue<bool>() != true
            || lifted?["name"]?.GetValue<string>() != "GetHashCode"
            || !IsEqualityHelper(1, "kotlinInstanceEquals") || !IsEqualityHelper(2, "kotlinInstanceHashCode")
            || superCall?["k"]?.GetValue<string>() != "callInstance" || superCall["method"]?.GetValue<string>() != "Equals"
            || superCall["anySlot"]?.GetValue<bool>() != true || superCall["super"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("materialized BIR payload normalization self-test failed");

        var first = payload.DeepClone();
        Normalize(payload);
        if (!JsonNode.DeepEquals(first, payload))
            throw new InvalidOperationException("materialized BIR payload normalization is not idempotent");
    }
}
