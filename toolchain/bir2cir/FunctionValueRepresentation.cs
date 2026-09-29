using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Ordinary Kotlin function variance must not change the identity of the stored value.
// Choose argument/result carriers at construction, rather than allocating adapters at
// each assignment or generic use. A function carrying an explicit physical delegate
// family retains its exact signature.
static class FunctionValueRepresentation
{
    static readonly TypeNode Object = new TypeNode.Fqn("object");

    public static void Apply(JsonNode root)
    {
        Walk(root);
    }

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["t"] is JsonValue tag && tag.TryGetValue<string>(out var kind) && kind == "fn")
            {
                var fn = (TypeNode.Fn)TypeJson.Read(obj);
                if (fn.Suspend || fn.Clr != null) return;
                var physical = new TypeNode.Fn(false, Object,
                    fn.Params.Select(_ => Object).ToArray(), fn.Recv == null ? null : Object);
                var replacement = (JsonObject)TypeJson.Write(physical);
                obj.Clear();
                foreach (var pair in replacement.ToList()) obj[pair.Key] = pair.Value?.DeepClone();
                return;
            }
            foreach (var pair in obj.ToList())
            {
                // These are authored Kotlin facts, not physical slots.
                if (pair.Key is DeclarationIdentityBinding.SemanticSignatureKey or "attrs" or "retAttrs") continue;
                if (pair.Value != null) Walk(pair.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child != null) Walk(child);
    }
}
