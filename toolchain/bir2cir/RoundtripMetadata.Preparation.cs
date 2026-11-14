using System.Text.Json.Nodes;

static partial class RoundtripMetadata
{
    // Grant obligations on the actual old-phase declaration occurrences only.
    // This is structural ownership transfer, not metadata interpretation: the
    // typed completion owns flags, carrier payloads and attribute filtering.
    internal static void PrepareMetadata(JsonNode root, bool runtimeMode)
    {
        if (root is not JsonObject file) return;
        file["roundtripMetadataPending"] = true;
        Members(file);
        Types(file["types"] as JsonArray);

        void Types(JsonArray types)
        {
            if (types == null) return;
            foreach (var type in types.OfType<JsonObject>())
            {
                type["roundtripTypeHeaderPending"] = true;
                if (runtimeMode) type.Remove(KotlinSupertypesRecord.PreKey);
                else
                {
                    if (type["kind"]?.GetValue<string>() == "enum") continue;
                    Move(type, type, KotlinSupertypesRecord.PreKey, "roundtripSupertypesPre");
                }
                Members(type);
                Types(type["types"] as JsonArray);
            }
        }

        void Members(JsonObject owner)
        {
            Declarations(owner["methods"] as JsonArray, callable: true, method: true);
            Declarations(owner["fields"] as JsonArray);
            Declarations(owner["properties"] as JsonArray);
            Declarations(owner["ctors"] as JsonArray, callable: true);
        }

        void Declarations(JsonArray declarations, bool callable = false, bool method = false)
        {
            if (declarations == null) return;
            foreach (var declaration in declarations.OfType<JsonObject>())
            {
                declaration["roundtripMetadataPending"] = true;
                if (!callable) continue;
                var identity = new JsonObject();
                foreach (var key in new[] { DeclarationIdentityBinding.Key, "declarationSourceName",
                    DeclarationSignatureIdentity.AllocatedKey, DeclarationIdentityBinding.SemanticSignatureKey,
                    ReifiedNullabilityWitnessLowering.SemanticIndicesKey,
                    ReifiedNullabilityWitnessLowering.WitnessIndicesKey, NullableRepresentationTypes.MethodFrameKey })
                    Move(declaration, identity, key, key);
                if (identity.Count != 0) declaration["roundtripDeclarationIdentity"] = identity;
                if (method)
                {
                    if (runtimeMode) declaration.Remove(NullableGenericErasure.MethodTypeParameterBoundsPre);
                    else Move(declaration, declaration, NullableGenericErasure.MethodTypeParameterBoundsPre, "roundtripMethodBounds");
                }
                Declarations(declaration["params"] as JsonArray);
            }
        }
    }

    static void Move(JsonObject source, JsonObject target, string sourceKey, string targetKey)
    {
        if (source.Remove(sourceKey, out var value)) target[targetKey] = value;
    }
}
