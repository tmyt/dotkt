using System.Text.Json.Nodes;
using DotKt.Bir;

// Capture the Kotlin declaration before any representation pass can replace it with a wider CLR entry point.
// Field visibility is explicit in BIR, including both source properties and private backing storage.
static class CallableVisibilityLowering
{
    internal const string Carrier = "DotKt.Runtime.CompilerServices.KotlinCallableVisibilityAttribute";
    const string PublishedApi = "kotlin.PublishedApi";

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var owners = roots.OfType<JsonObject>().SelectMany(Owners).ToArray();
        foreach (var owner in owners) Apply(owner);

        // `overrides` is the frontend's complete declaration closure, including exact getter/setter identities.
        // A selected public CLR slot requires a public override even when its Kotlin declaration stays internal.
        // Snapshot the already-promoted slots so this decision is independent of source/file traversal order;
        // neither matching member names nor repeating the base declaration's annotation establishes this edge.
        var methods = owners.Where(owner => owner["methods"] is JsonArray)
            .SelectMany(owner => ((JsonArray)owner["methods"]).OfType<JsonObject>()).ToArray();
        var publicSlots = methods.Where(method => (method["vis"]?.GetValue<string>() ?? "public") == "public")
            .Select(method => method[DeclarationIdentityBinding.Key]?.GetValue<string>())
            .Where(id => id != null).ToHashSet(StringComparer.Ordinal);
        foreach (var method in methods)
            if (method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>().Any(edge =>
                edge[DeclarationIdentityBinding.Key]?.GetValue<string>() is string id
                && (publicSlots.Contains(id) || refs.IsPublicVirtualDeclaration(id))))
                method["vis"] = "public";
    }

    static IEnumerable<JsonObject> Owners(JsonObject owner)
    {
        yield return owner;
        if (owner["types"] is JsonArray types)
            foreach (var child in types.OfType<JsonObject>().SelectMany(Owners)) yield return child;
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
