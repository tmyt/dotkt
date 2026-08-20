using System.Text.Json.Nodes;

// A Kotlin enum entry is semantic declaration identity: owner + entry name. A referenced rich Kotlin enum carries an
// explicit producer map from that identity to its singleton field; a basic local enum uses its contiguous ordinal,
// while a referenced C# enum may be sparse, negative, aliased, or UInt64-backed. Only bir2cir may translate each of
// those Kotlin identities to the selected reference DLL's physical representation.
static class EnumValueLowering
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, ISet<string> localBasicEnums) =>
        Walk(root, refs, localBasicEnums);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, ISet<string> localBasicEnums)
    {
        if (node is JsonObject obj)
        {
            var kind = obj["k"]?.GetValue<string>();
            if (kind == "enumValue"
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

            if (kind is "enumName" or "enumOrdinal"
                && obj["e"] is JsonNode receiver
                && obj["type"] is JsonNode enumType
                && TypeJson.OwnerName(enumType) is string enumOwner)
            {
                if (refs.TryKotlinRichEnumInstanceFields(enumOwner, out var nameField, out var ordinalField))
                {
                    var physicalField = kind == "enumName" ? nameField : ordinalField;
                    var receiverClone = receiver.DeepClone();
                    var typeClone = enumType.DeepClone();
                    obj.Clear();
                    obj["k"] = "field";
                    obj["ownerType"] = typeClone;
                    obj["recv"] = receiverClone;
                    obj["name"] = physicalField;
                }
                else if (kind == "enumName")
                {
                    var receiverClone = receiver.DeepClone();
                    obj.Clear();
                    obj["k"] = "objMethod";
                    obj["method"] = "toString";
                    obj["recv"] = receiverClone;
                }
                else if (localBasicEnums.Contains(enumOwner))
                {
                    // A local basic enum's physical value is its contiguous Kotlin ordinal. The type-bearing form is
                    // reserved for referenced CLR enums, whose declaration index may differ from the underlying value.
                    obj.Remove("type");
                }
            }
            foreach (var kv in obj)
                if (kv.Value != null) Walk(kv.Value, refs, localBasicEnums);
        }
        else if (node is JsonArray arr)
            foreach (var item in arr)
                if (item != null) Walk(item, refs, localBasicEnums);
    }
}

sealed record EnumPhysicalConstant(string Underlying, string Value);
