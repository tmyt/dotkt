using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin `const` is a semantic declaration fact in BIR. Its CLR representation is a literal field whose default
// value lives in metadata, not executable type-initializer code. Resolve that representation here, before ordinary
// type lowering, so ilemit only has to emit the resulting CIR field one-to-one and dll2klib can recover the constant
// from the ECMA-335 Constant table.
static class ConstFieldLowering
{
    sealed record Literal(TypeNode Type, JsonNode Value);

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs, bool referenceBuild)
    {
        var local = new Dictionary<(string Owner, string Name), Literal>();
        foreach (var root in roots.OfType<JsonObject>())
            LowerDeclarations(root, Str(root["fileClass"]), referenceBuild, local);
        foreach (var root in roots) RewriteReads(root, local, refs);
    }

    static void LowerDeclarations(JsonObject container, string ownerName, bool referenceBuild,
        Dictionary<(string Owner, string Name), Literal> local)
    {
        if (container["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
            {
                // Keep the semantic declaration type for local read folding. A reference build lowers the field's
                // physical Constant-table slot below, but an inlined Kotlin const expression still has its source type.
                var declarationType = TypeJson.Read(field["type"]);
                if (Lower(field, referenceBuild) && ownerName != null && Str(field["name"]) is string fieldName)
                    local.Add((ownerName, fieldName), new Literal(
                        declarationType ?? throw new InvalidOperationException(
                            $"const field '{ownerName}.{fieldName}' has no declaration type"),
                        field["constant"]?.DeepClone()));
            }

        if (container["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>())
                LowerDeclarations(type, Str(type["name"]), referenceBuild, local);
    }

    static bool Lower(JsonObject field, bool referenceBuild)
    {
        if (!Bool(field["const"])) return false;

        var name = Str(field["name"]) ?? "<unnamed>";
        if (!Bool(field["static"]))
            throw new InvalidOperationException($"const field '{name}' is not static");
        if (field["init"] is not JsonObject init || Str(init["k"]) != "const" || !init.ContainsKey("value"))
            throw new InvalidOperationException($"const field '{name}' has no compile-time constant initializer");

        if (referenceBuild)
        {
            // The metadata stdlib keeps Kotlin declaration types as local TypeDefs, but ECMA-335 permits a Literal
            // only at one of its scalar/string physical types. Preserve the exact Kotlin slot in the trusted carrier
            // and give the Constant-table declaration its concrete CLR type. dll2klib projects KotlinType back before
            // exposing the declaration, so ref/runtime KLIBs agree without executable reference-assembly initializers.
            var semanticType = TypeJson.Read(field["type"])
                ?? throw new InvalidOperationException($"const field '{name}' has no declaration type");
            field["kotlinType"] = TypeJson.Write(semanticType).ToJsonString();
            field["type"] = TypeJson.Write(BirTypeLowering.LowerType(
                semanticType, refBuild: false, force: true, typeArg: false));
        }

        field["constant"] = init["value"]?.DeepClone();
        field.Remove("init");
        field.Remove("const");
        return true;
    }

    static void RewriteReads(JsonNode node, IReadOnlyDictionary<(string Owner, string Name), Literal> local,
        ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.ToArray())
                    if (child.Value != null) RewriteReads(child.Value, local, refs);
                if (Str(obj["k"]) != "staticField" || TypeJson.Read(obj["ownerType"]) is not TypeNode.Fqn owner
                    || Str(obj["name"]) is not string name)
                    return;
                Literal literal = null;
                if (!local.TryGetValue((owner.Name, name), out literal)
                    && refs.TryResolveLiteralField(owner, name, out var externalType, out var externalValue))
                    literal = new Literal(externalType, externalValue);
                if (literal == null) return;
                obj.Clear();
                obj["k"] = "const";
                obj["type"] = TypeJson.Write(literal.Type);
                obj["value"] = literal.Value?.DeepClone();
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) RewriteReads(child, local, refs);
                break;
        }
    }

    static bool Bool(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}
