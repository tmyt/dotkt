using System.Text.Json.Nodes;

// A basic Kotlin enum entry is semantic declaration identity: owner + entry name. For a local Kotlin enum its
// declaration ordinal is also the contiguous CLR value. A referenced C# enum may be sparse, negative, aliased, or
// UInt64-backed, so only bir2cir may translate that identity to the selected reference DLL's physical constant.
static class EnumValueLowering
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) => Walk(root, refs);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            if (obj["k"]?.GetValue<string>() == "enumValue"
                && obj["entry"]?.GetValue<string>() is string entry
                && TypeJson.OwnerName(obj["type"]) is string owner
                && refs.ResolveNetEnumConstant(owner, entry) is EnumPhysicalConstant physical)
            {
                obj["underlying"] = physical.Underlying;
                obj["physicalValue"] = physical.Value;
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
