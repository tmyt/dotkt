using System.Text.Json.Nodes;
using DotKt.Bir;

// `lateinitGet` is the Kotlin null-check fact. Resolve its failure path to an ordinary constructor expression here,
// where Kotlin semantics become a concrete CLR representation. The nested `new` then follows the same exact local or
// referenced constructor-binding pipeline as every source construction; ilemit only evaluates and throws that CIR.
static class LateinitGetLowering
{
    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) Walk(root);
    }

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.ToArray())
                    if (child.Value != null) Walk(child.Value);
                if (Str(obj["k"]) == "lateinitGet" && obj["exception"] == null)
                {
                    var name = Str(obj["lateinitSourceName"]) ?? Str(obj["name"])
                        ?? throw new InvalidOperationException("lateinitGet has no property name");
                    var stringType = TypeJson.Fqn("kotlin.String");
                    obj["exception"] = new JsonObject {
                        ["k"] = "new",
                        ["type"] = TypeJson.Fqn("kotlin.UninitializedPropertyAccessException"),
                        ["argTypes"] = new JsonArray(stringType.DeepClone()),
                        ["args"] = new JsonArray(new JsonObject {
                            ["k"] = "const",
                            ["type"] = stringType,
                            ["value"] = $"lateinit property {name} has not been initialized",
                        }),
                    };
                    obj.Remove("lateinitSourceName");
                }
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) Walk(child);
                break;
        }
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
