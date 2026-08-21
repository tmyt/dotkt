using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A declaration edge or MethodImpl owner is CLR metadata, not a Kotlin lookup token. Keep every referenced
// base/interface and descriptor owner on the exact TypeDef identity selected by bir2cir. In particular, a semantic
// @ClrTypeAlias edge (`ICollection` with one structured argument) and a descriptor owner (`ICollection`1`) must not
// reach ilemit as two spellings of the same interface: walking both would emit the same MethodImpl twice. Nested
// external identities also retain their `Outer`N+Inner`M ownership instead of asking ilemit to reconstruct it from a
// flattened source name.
static class ExactExternalDeclarationIdentity
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            Rewrite(type, "base", refs);
            if (type["interfaces"] is JsonArray interfaces)
                for (var i = 0; i < interfaces.Count; i++)
                {
                    var current = interfaces[i];
                    var exact = Exact(current, refs);
                    if (!ReferenceEquals(current, exact)) interfaces[i] = exact;
                }
            if (type["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                {
                    RewriteMethodImplOwners(method, "clrInterfaceImpls", refs);
                    RewriteMethodImplOwners(method, "clrBaseImpls", refs);
                }
            Apply(type, refs);
        }
    }

    static void RewriteMethodImplOwners(JsonObject method, string key, ReferenceMetadataIndex refs)
    {
        if (method[key] is not JsonArray implementations) return;
        foreach (var implementation in implementations.OfType<JsonObject>())
            Rewrite(implementation, "owner", refs);
    }

    static void Rewrite(JsonObject owner, string key, ReferenceMetadataIndex refs)
    {
        if (owner[key] is not JsonNode value) return;
        var exact = Exact(value, refs);
        if (!ReferenceEquals(value, exact)) owner[key] = exact;
    }

    static JsonNode Exact(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(node) is not TypeNode.Fqn type) return node;
        var exact = refs.ExactReflectedOwner(type.Name, type.Args?.Length ?? 0);
        return exact == type.Name ? node : TypeJson.Write(new TypeNode.Fqn(exact, type.Args));
    }
}
