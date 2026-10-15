using System.Text.Json.Nodes;
using DotKt.Bir;

// Capture the Kotlin declaration before any representation pass can replace it with a wider CLR entry point.
// Fields here represent source properties; their backing-storage visibility is already explicit in BIR.
static class CallableVisibilityLowering
{
    internal const string Carrier = "DotKt.Runtime.CompilerServices.KotlinCallableVisibilityAttribute";
    const string PublishedApi = "kotlin.PublishedApi";

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots.OfType<JsonObject>()) Apply(root);
    }

    static void Apply(JsonObject owner)
    {
        foreach (var key in new[] { "methods", "ctors", "fields" })
            if (owner[key] is JsonArray declarations)
                foreach (var declaration in declarations.OfType<JsonObject>())
                {
                    Preserve(declaration);
                    Promote(declaration);
                }
        if (owner["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) Apply(type);
    }

    public static void Preserve(JsonObject declaration)
    {
        var visibility = declaration["vis"]?.GetValue<string>() ?? "public";
        if (visibility != "public") RoundtripMetadata.PreserveCallableVisibility(declaration, visibility);
    }

    static void Promote(JsonObject declaration)
    {
        if (declaration["vis"]?.GetValue<string>() == "internal" &&
            declaration["attrs"] is JsonArray attrs && attrs.OfType<JsonObject>().Any(attribute =>
                TypeJson.OwnerName(attribute["attr"]) == PublishedApi))
            declaration["vis"] = "public";
    }

    // A representation-created source entry point must retain declaration visibility independently of its physical
    // access. In particular, a suspend Task bridge is public even when the Kotlin declaration is not.
    public static void Copy(JsonObject source, JsonObject target, string visibility = null)
    {
        if (source["attrs"] is JsonArray attributes)
            foreach (var attribute in attributes.OfType<JsonObject>())
            {
                var name = TypeJson.OwnerName(attribute["attr"]);
                if (name != PublishedApi && (name != Carrier || visibility != null)) continue;
                if (target["attrs"] is not JsonArray) target["attrs"] = new JsonArray();
                ((JsonArray)target["attrs"]).Add(attribute.DeepClone());
            }
        if (visibility != null && visibility != "public")
            RoundtripMetadata.PreserveCallableVisibility(target, visibility);
        Promote(target);
    }
}
