using System.Text.Json.Nodes;

// Expose independent synthetic declaration scopes to frame materialization without consuming
// the closure facts needed by the later inline and nullable-witness passes.
static class PreparedClosureDefaultFrames
{
    internal sealed record StageResult(IReadOnlyList<JsonNode> Types, Action Restore);

    public static StageResult Stage(JsonObject file, IEnumerable<JsonNode> inputs, ReferenceMetadataIndex refs)
    {
        var roots = inputs.ToArray();
        var sources = new Dictionary<JsonObject, JsonObject>();
        void Find(JsonNode node)
        {
            if (node is JsonObject expression)
            {
                if (expression["k"]?.GetValue<string>() is "newClosure" or "newSam"
                    && expression["synthClass"] is JsonObject source)
                    sources.TryAdd(expression, (JsonObject)source.DeepClone());
                foreach (var (key, child) in expression) if (key != "attrs") Find(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Find(child);
        }
        foreach (var root in roots) Find(root);
        var types = ClosureSynthesis.ApplyMaterialized(file, roots, refs);
        return new StageResult(types, () => {
            var declarations = types.OfType<JsonObject>().ToDictionary(type => type["name"].GetValue<string>());
            var ingredients = new Dictionary<string, JsonObject>();
            foreach (var (expression, source) in sources)
            {
                var name = TypeJson.OwnerName(expression["closureType"] ?? expression["samType"]);
                if (!ingredients.TryAdd(name, source) && !JsonNode.DeepEquals(ingredients[name], source))
                    throw new InvalidOperationException("Conflicting prepared closure declarations: " + name);
            }
            void RestoreNode(JsonNode node)
            {
                if (node is JsonArray array)
                {
                    foreach (var child in array.ToArray()) RestoreNode(child);
                    return;
                }
                if (node is not JsonObject expression) return;
                var name = TypeJson.OwnerName(expression["closureType"] ?? expression["samType"]);
                if (expression["synthClass"] == null && name != null
                    && declarations.TryGetValue(name, out var declaration))
                {
                    JsonObject restored;
                    if (expression["k"]?.GetValue<string>() == "newClosure")
                    {
                        restored = (JsonObject)ingredients[name].DeepClone();
                        foreach (var key in new[] { "name", "fields", "typeParams", "semanticOwner",
                            "outerTypeParamCount", "outerTypeParamOffset", KotlinSupertypesRecord.PreKey })
                        {
                            if (declaration[key] != null) restored[key] = declaration[key].DeepClone();
                            else restored.Remove(key);
                        }
                        var invoke = ((JsonArray)declaration["methods"]).OfType<JsonObject>()
                            .Single(method => method["name"]?.GetValue<string>() == "invoke");
                        foreach (var key in new[] { "params", "ret", "body", "nullableGenericRet", "retNullableFlags" })
                            if (invoke[key] != null) restored[key] = invoke[key].DeepClone();
                    }
                    else restored = (JsonObject)declaration.DeepClone();
                    ClosureSynthesis.MarkPreboundFrame(restored);
                    expression["synthClass"] = restored;
                }
                foreach (var (key, child) in expression.ToArray()) if (key != "attrs") RestoreNode(child);
            }
            foreach (var root in roots) RestoreNode(root);
            foreach (var type in types) ((JsonArray)file["types"]).Remove(type);
        });
    }
}
