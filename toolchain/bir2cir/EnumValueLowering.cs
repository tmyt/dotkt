using System.Text.Json.Nodes;

// A Kotlin enum entry is semantic declaration identity: owner + entry name. A referenced rich Kotlin enum carries an
// explicit producer map from that identity to its singleton field; a basic local enum uses its contiguous ordinal,
// while a referenced C# enum may be sparse, negative, aliased, or UInt64-backed. Only bir2cir may translate each of
// those Kotlin identities to the selected reference DLL's physical representation.
static class EnumValueLowering
{
    public static void Apply(
        JsonNode root,
        ReferenceMetadataIndex refs,
        ISet<string> localBasicEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localExplicitEnums) =>
        Walk(root, refs, localBasicEnums, localExplicitEnums);

    static void Walk(
        JsonNode node,
        ReferenceMetadataIndex refs,
        ISet<string> localBasicEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localExplicitEnums)
    {
        if (node is JsonObject obj)
        {
            var kind = obj["k"]?.GetValue<string>();
            if (kind == "enumValue"
                && obj["entry"]?.GetValue<string>() is string entry
                && obj["type"] is JsonNode type
                && TypeJson.OwnerName(type) is string owner)
            {
                if (localExplicitEnums.TryGetValue(owner, out var localExplicit))
                {
                    var physical = localExplicit.Entries.SingleOrDefault(candidate => candidate.Name == entry)
                        ?? throw new InvalidOperationException($"bir2cir: explicit enum '{owner}' has no entry '{entry}'");
                    obj["underlying"] = localExplicit.Underlying;
                    obj["physicalValue"] = physical.PhysicalValue;
                }
                else if (refs.TryKotlinRichEnumEntryField(owner, entry, out var field))
                {
                    foreach (var key in obj.Select(kv => kv.Key).ToList()) obj.Remove(key);
                    obj["k"] = "staticField";
                    obj["ownerType"] = type.DeepClone();
                    obj["name"] = field;
                }
                else if (refs.TryKotlinBasicEnum(owner, out var referencedExplicit))
                {
                    var physical = referencedExplicit.Entries.SingleOrDefault(candidate => candidate.Name == entry)
                        ?? throw new InvalidOperationException($"bir2cir: explicit enum '{owner}' has no entry '{entry}'");
                    obj["underlying"] = referencedExplicit.Underlying;
                    obj["physicalValue"] = physical.PhysicalValue;
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
                    obj["method"] = "ToString";
                    obj["recv"] = receiverClone;
                }
                else if (kind == "enumOrdinal"
                    && localExplicitEnums.TryGetValue(enumOwner, out var localExplicit))
                {
                    obj["values"] = Values(localExplicit);
                }
                else if (kind == "enumOrdinal" && refs.TryKotlinBasicEnum(enumOwner, out var referencedExplicit))
                {
                    obj["values"] = Values(referencedExplicit);
                }
                else if (localBasicEnums.Contains(enumOwner))
                {
                    // A local basic enum's physical value is its contiguous Kotlin ordinal. The type-bearing form is
                    // reserved for referenced CLR enums, whose declaration index may differ from the underlying value.
                    obj.Remove("type");
                }
            }
            if (kind == "enumValues" && TypeJson.OwnerName(obj["type"]) is string valuesOwner
                && (localExplicitEnums.TryGetValue(valuesOwner, out var localValues)
                    || refs.TryKotlinBasicEnum(valuesOwner, out localValues)))
                obj["values"] = Values(localValues);

            foreach (var kv in obj)
                if (kv.Value != null) Walk(kv.Value, refs, localBasicEnums, localExplicitEnums);
        }
        else if (node is JsonArray arr)
            foreach (var item in arr)
                if (item != null) Walk(item, refs, localBasicEnums, localExplicitEnums);
    }

    static JsonArray Values(BasicEnumMetadata metadata) => new(metadata.Entries.Select(entry => (JsonNode)new JsonObject
    {
        ["ordinal"] = entry.Ordinal,
        ["underlying"] = metadata.Underlying,
        ["physicalValue"] = entry.PhysicalValue,
    }).ToArray());
}

sealed record EnumPhysicalConstant(string Underlying, string Value);
