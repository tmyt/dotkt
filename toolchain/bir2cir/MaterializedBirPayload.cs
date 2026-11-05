using System.Text.Json.Nodes;
using DotKt.Bir;

// Opaque executable BIR crosses an ownership boundary in KotlinInline/KotlinDefault carriers. The producer stores
// frontend vocabulary intentionally; the consuming bir2cir instance must put that payload through the same
// representation-entry contract as source BIR before any payload-specific rewrite inspects or clones it.
//
// Keep this chokepoint small and ordered. A new representation-entry invariant belongs here when it must hold for
// both inline and default payloads; callers must not repair a materialized payload later with an idempotent file-wide
// sweep. Normal source BIR takes the same first step directly in Program's phase-1 entry.
static class MaterializedBirPayload
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

    // KotlinDefault can also be nested as an opaque string on a parameter of an inline carrier.
    public static void HideDefaultSourceSpans(JsonObject obj)
    {
        if (obj["attr"] == null || TypeJson.OwnerName(obj["attr"]) != "kotlin.clr.KotlinDefault"
            || obj["args"] is not JsonArray { Count: >= 2 } args || args[1] is not JsonObject argument
            || argument["value"] is not JsonValue value || !value.TryGetValue<string>(out var json))
            return;
        var expression = JsonNode.Parse(json, documentOptions: BirJson.DocOptions);
        HideSourceSpans(expression);
        argument["value"] = expression.ToJsonString();
    }

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
        Normalize(payload);
        var call = payload["body"]?[0] as JsonObject;
        var lifted = payload["lifted"]?[0] as JsonObject;
        if (call?["method"]?.GetValue<string>() != "ToString"
            || call["anySlot"]?.GetValue<bool>() != true
            || lifted?["name"]?.GetValue<string>() != "GetHashCode")
            throw new InvalidOperationException("materialized BIR payload normalization self-test failed");
    }
}
