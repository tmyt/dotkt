using System.Text.Json.Nodes;

// A Kotlin enum entry is semantic declaration identity: owner + entry name. A referenced rich Kotlin enum carries an
// explicit producer map from that identity to its singleton field; a basic local enum uses its contiguous ordinal,
// while a referenced C# enum may be sparse, negative, aliased, or UInt64-backed. Only bir2cir may translate each of
// those Kotlin identities to the selected reference DLL's physical representation.
static class EnumValueLowering
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            if (obj["k"]?.GetValue<string>() == "enumValue"
                && obj["entry"]?.GetValue<string>() is string entry
                && obj["type"] is JsonNode type
                && TypeJson.OwnerName(type) is string owner)
            {
                if (refs.TryKotlinRichEnumEntryField(owner, entry, out var field))
                {
                    foreach (var key in obj.Select(kv => kv.Key).ToList()) obj.Remove(key);
                    obj["k"] = "staticField";
                    obj["ownerType"] = type.DeepClone();
                    obj["name"] = field;
                }
                else if (refs.ResolveNetEnumConstant(owner, entry) is EnumPhysicalConstant physical)
                {
                    obj["underlying"] = physical.Underlying;
                    obj["physicalValue"] = physical.Value;
                }
            }
            foreach (var kv in obj)
                if (kv.Value != null) Walk(kv.Value, refs);
        }
        else if (node is JsonArray arr)
            foreach (var item in arr)
                if (item != null) Walk(item, refs);
    }
}

sealed record EnumPhysicalConstant(string Underlying, string Value);
