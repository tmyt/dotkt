using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin enum entry is semantic declaration identity: owner + entry name. A referenced rich Kotlin enum carries an
// explicit producer map from that identity to its singleton field; a basic local enum uses its contiguous ordinal,
// while a referenced C# enum may be sparse, negative, aliased, or UInt64-backed. Only bir2cir may translate each of
// those Kotlin identities to the selected reference DLL's physical representation.
static class EnumValueLowering
{
    public static bool Apply(
        JsonNode root,
        ReferenceMetadataIndex refs,
        ISet<string> localBasicEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localExplicitEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localParseEnums)
    {
        var runtimeLookup = false;
        Walk(root, refs, localBasicEnums, localExplicitEnums, localParseEnums, ref runtimeLookup);
        return runtimeLookup;
    }

    static void Walk(
        JsonNode node,
        ReferenceMetadataIndex refs,
        ISet<string> localBasicEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localExplicitEnums,
        IReadOnlyDictionary<string, BasicEnumMetadata> localParseEnums, ref bool runtimeLookup)
    {
        if (node is JsonObject obj)
        {
            var kind = obj["k"]?.GetValue<string>();
            // Lifted property-reference getters preserve the selected inherited Enum property as an ordinary
            // property call. A referenced rich enum has no physical name/ordinal getter: its producer supplies the
            // exact field identities. Consume that same authority as the semantic enumName/enumOrdinal path.
            if (kind == "callInstance" && obj["prop"]?.GetValue<string>() == "get"
                && obj["method"]?.GetValue<string>() is "name" or "ordinal"
                && obj["args"] is JsonArray { Count: 0 }
                && obj["recv"] is JsonNode propertyReceiver && obj["ownerType"] is JsonNode propertyOwner
                && TypeJson.OwnerName(propertyOwner) is string propertyEnum
                && refs.TryKotlinRichEnumInstanceFields(propertyEnum, out var propertyNameField, out var propertyOrdinalField))
                RewriteRichField(obj, propertyReceiver, propertyOwner,
                    obj["method"].GetValue<string>() == "name" ? propertyNameField : propertyOrdinalField,
                    obj["method"].GetValue<string>() == "name" ? "kotlin.String" : "kotlin.Int");
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

            if (kind == "enumParse"
                && obj["type"] is JsonNode parseType
                && obj["arg"] is JsonNode parseArg
                && TypeJson.OwnerName(parseType) is string parseOwner
                && (localParseEnums.TryGetValue(parseOwner, out var localParse)
                    || refs.TryKotlinBasicEnum(parseOwner, out localParse)
                    || refs.TryNativeEnumEntries(parseOwner, out localParse)))
            {
                RewriteExplicitParse(obj, parseType, parseArg, parseOwner, localParse);
            }

            if (kind == "enumParse" && TypeJson.Read(obj["type"]) is TypeNode.Tv parseParameter)
            {
                runtimeLookup = true;
                var argument = obj["arg"].DeepClone();
                var position = obj["pos"]?.DeepClone();
                obj.Clear();
                obj["k"] = "cast";
                obj["type"] = TypeJson.Write(parseParameter);
                obj["e"] = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.EnumRuntimeKt"),
                    ["method"] = "kotlinEnumValueOf",
                    ["sig"] = new JsonArray(TypeJson.Fqn("kotlin.Any"), TypeJson.Fqn("kotlin.String")),
                    ["args"] = new JsonArray(
                        new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(parseParameter) }, argument),
                    ["ret"] = TypeJson.Fqn("kotlin.Any"),
                };
                if (position != null) obj["pos"] = position;
            }

            if (kind is "enumName" or "enumOrdinal"
                && obj["e"] is JsonNode receiver
                && obj["type"] is JsonNode enumType
                && TypeJson.OwnerName(enumType) is string enumOwner)
            {
                if (refs.TryKotlinRichEnumInstanceFields(enumOwner, out var nameField, out var ordinalField))
                {
                    var physicalField = kind == "enumName" ? nameField : ordinalField;
                    RewriteRichField(obj, receiver, enumType, physicalField,
                        kind == "enumName" ? "kotlin.String" : "kotlin.Int");
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
                else if (refs.TryNativeEnumEntries(enumOwner, out var nativeEntries))
                {
                    obj["values"] = Values(nativeEntries);
                }
            }
            if (kind == "enumValues" && TypeJson.OwnerName(obj["type"]) is string valuesOwner
                && (localExplicitEnums.TryGetValue(valuesOwner, out var localValues)
                    || refs.TryKotlinBasicEnum(valuesOwner, out localValues)
                    || refs.TryNativeEnumEntries(valuesOwner, out localValues)))
                obj["values"] = Values(localValues);

            if (kind == "enumValues" && TypeJson.Read(obj["type"]) is TypeNode.Tv parameter)
            {
                runtimeLookup = true;
                // The enum declaration is known only at runtime. Select the same ordered runtime operation here;
                // ilemit must not substitute Enum.GetValues' numeric order for Kotlin's declaration order.
                var position = obj["pos"]?.DeepClone();
                obj.Clear();
                obj["k"] = "cast";
                obj["type"] = TypeJson.Write(new TypeNode.Array(parameter));
                obj["e"] = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.EnumRuntimeKt"),
                    ["method"] = "kotlinEnumValues",
                    ["sig"] = new JsonArray(TypeJson.Fqn("kotlin.Any")),
                    ["args"] = new JsonArray(new JsonObject { ["k"] = "classRef", ["type"] = TypeJson.Write(parameter) }),
                    ["ret"] = TypeJson.Fqn("kotlin.Any"),
                };
                if (position != null) obj["pos"] = position;
            }

            foreach (var kv in obj)
                if (kv.Value != null) Walk(kv.Value, refs, localBasicEnums, localExplicitEnums, localParseEnums, ref runtimeLookup);
        }
        else if (node is JsonArray arr)
            foreach (var item in arr)
                if (item != null) Walk(item, refs, localBasicEnums, localExplicitEnums, localParseEnums, ref runtimeLookup);
    }

    static void RewriteRichField(JsonObject node, JsonNode receiver, JsonNode owner, string field, string resultType)
    {
        var receiverClone = receiver.DeepClone();
        var ownerClone = owner.DeepClone();
        node.Clear();
        node["k"] = "field";
        node["ownerType"] = ownerClone;
        node["recv"] = receiverClone;
        node["name"] = field;
        node["ret"] = TypeJson.Fqn(resultType);
    }

    static JsonArray Values(BasicEnumMetadata metadata) => new(metadata.Entries.Select(entry => (JsonNode)new JsonObject
    {
        ["ordinal"] = entry.Ordinal,
        ["underlying"] = metadata.Underlying,
        ["physicalValue"] = entry.PhysicalValue,
    }).ToArray());

    internal static JsonObject ReferencedOrdinal(JsonNode receiver, JsonNode enumType, ReferenceMetadataIndex refs)
    {
        var owner = TypeJson.OwnerName(enumType);
        if (owner == null || !(refs.TryKotlinBasicEnum(owner, out var metadata)
                || refs.TryNativeEnumEntries(owner, out metadata)))
            throw new InvalidOperationException("Referenced enum ordinal has no selected declaration map");
        return new JsonObject
        {
            ["k"] = "enumOrdinal", ["e"] = receiver.DeepClone(),
            ["type"] = enumType.DeepClone(), ["values"] = Values(metadata),
        };
    }

    static int _parseTemp;

    // System.Enum.Parse also accepts numeric strings and, for [Flags] enums, comma-separated combinations. Kotlin's
    // valueOf contract accepts one exact declared entry name only. The trusted ordered producer map is therefore the
    // complete decision table: evaluate the source name once, compare it to every declaration, and materialize the
    // exact physical constant already selected by bir2cir. Failure stays an ordinary Kotlin exception expression so
    // the later alias/member-resolution passes bind it exactly like a source-authored throw.
    static void RewriteExplicitParse(
        JsonObject node,
        JsonNode enumType,
        JsonNode argument,
        string owner,
        BasicEnumMetadata metadata)
    {
        var tempName = "__enumParse$" + System.Threading.Interlocked.Increment(ref _parseTemp);
        var tempType = TypeJson.Fqn("kotlin.String");
        JsonNode result = new JsonObject
        {
            ["k"] = "throwExpr",
            ["value"] = new JsonObject
            {
                ["k"] = "new",
                ["type"] = TypeJson.Fqn("kotlin.IllegalArgumentException"),
                ["argTypes"] = new JsonArray
                {
                    TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String"))),
                },
                ["args"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "const", ["type"] = tempType.DeepClone(),
                        ["value"] = $"No enum constant {owner}",
                    },
                },
            },
        };
        foreach (var entry in metadata.Entries.Reverse())
        {
            result = new JsonObject
            {
                ["k"] = "cond",
                ["type"] = enumType.DeepClone(),
                ["cond"] = new JsonObject
                {
                    ["k"] = "objEq",
                    ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = tempName },
                    ["rhs"] = new JsonObject
                    {
                        ["k"] = "const", ["type"] = tempType.DeepClone(), ["value"] = entry.Name,
                    },
                },
                ["then"] = new JsonObject
                {
                    ["k"] = "enumValue",
                    ["type"] = enumType.DeepClone(),
                    ["entry"] = entry.Name,
                    ["ordinal"] = entry.Ordinal,
                    ["underlying"] = metadata.Underlying,
                    ["physicalValue"] = entry.PhysicalValue,
                },
                ["else"] = result,
            };
        }
        node.Clear();
        node["k"] = "valueBlock";
        node["type"] = enumType.DeepClone();
        node["stmts"] = new JsonArray
        {
            new JsonObject
            {
                ["k"] = "var", ["name"] = tempName, ["type"] = tempType, ["init"] = argument.DeepClone(),
            },
        };
        node["result"] = result;
    }
}

sealed record EnumPhysicalConstant(string Underlying, string Value);
