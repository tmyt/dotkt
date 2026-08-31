using System.Text.Json.Nodes;

// Opaque executable BIR crosses an ownership boundary in KotlinInline/KotlinDefault carriers. The producer stores
// frontend vocabulary intentionally; the consuming bir2cir instance must put that payload through the same
// representation-entry contract as source BIR before any payload-specific rewrite inspects or clones it.
//
// Keep this chokepoint small and ordered. A new representation-entry invariant belongs here when it must hold for
// both inline and default payloads; callers must not repair a materialized payload later with an idempotent file-wide
// sweep. Normal source BIR takes the same first step directly in Program's phase-1 entry.
static class MaterializedBirPayload
{
    public static void Normalize(JsonNode payload)
    {
        if (payload == null) return;
        ObjectSlotRename.Apply(payload);
    }

    public static void SelfTest()
    {
        var payload = new JsonObject
        {
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "callInstance",
                    ["method"] = "toString",
                    ["anySlot"] = true,
                },
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
        if (call?["method"]?.GetValue<string>() != "ToString"
            || call.ContainsKey("anySlot")
            || lifted?["name"]?.GetValue<string>() != "GetHashCode")
            throw new InvalidOperationException("materialized BIR payload normalization self-test failed");
    }
}
