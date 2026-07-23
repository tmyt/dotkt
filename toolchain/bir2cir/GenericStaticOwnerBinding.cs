using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize a legal CLR owner for a static member declared on a generic Kotlin class.
//
// Kotlin companion/static members cannot depend on the enclosing class's type parameters and represent one logical
// member (`Queue.REMOVE_FROZEN`, `Result.success`, ...). BIR therefore carries the faithful bare Kotlin owner. CLR
// metadata nevertheless requires every MemberRef parent for `G<T>` to be a TypeSpec; a bare generic definition (or
// `G<!0>` in a non-generic caller) is invalid. bir2cir selects the stable canonical `G<Any,...>` instantiation. ilemit
// then emits that owner literally. No source/library names participate in the decision.
static class GenericStaticOwnerBinding
{
    sealed class GenericStatics
    {
        public int Arity;
        public HashSet<string> Fields = new(StringComparer.Ordinal);
        public HashSet<string> Methods = new(StringComparer.Ordinal);
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var index = new Dictionary<string, GenericStatics>(StringComparer.Ordinal);
        foreach (var root in rootList) Collect(root, index);
        foreach (var root in rootList) Walk(root, index);
    }

    static void Collect(JsonNode node, Dictionary<string, GenericStatics> index)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var item in types.OfType<JsonObject>())
        {
            var name = Str(item["name"]);
            var arity = (item["typeParams"] as JsonArray)?.Count ?? 0;
            if (name != null && arity > 0)
            {
                var statics = new GenericStatics { Arity = arity };
                if (item["fields"] is JsonArray fields)
                    foreach (var field in fields.OfType<JsonObject>())
                        if (Bool(field["static"]) && Str(field["name"]) is string fn) statics.Fields.Add(fn);
                if (item["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                        if (Bool(method["static"]) && Str(method["name"]) is string mn) statics.Methods.Add(mn);
                index[name] = statics;
            }
            Collect(item, index);
        }
    }

    static void Walk(JsonNode node, Dictionary<string, GenericStatics> index)
    {
        switch (node)
        {
            case JsonObject obj:
                Bind(obj, index);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, index);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item, index);
                break;
        }
    }

    static void Bind(JsonObject node, Dictionary<string, GenericStatics> index)
    {
        var kind = Str(node["k"]);
        string ownerKey;
        bool isField;
        switch (kind)
        {
            case "staticField":
            case "setStaticField":
            case "setStaticFieldExpr":
                ownerKey = "ownerType";
                isField = true;
                break;
            case "callStatic":
                ownerKey = node["ownerType"] != null ? "ownerType" : "owner";
                isField = false;
                break;
            default:
                return;
        }

        if (TypeJson.Read(node[ownerKey]) is not TypeNode.Fqn { Args: null } owner) return;
        if (!index.TryGetValue(owner.Name, out var statics)) return;
        var member = Str(node[isField ? "name" : "method"]);
        if (member == null || !(isField ? statics.Fields.Contains(member) : statics.Methods.Contains(member))) return;
        node[ownerKey] = TypeJson.Write(new TypeNode.Fqn(owner.Name,
            Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Any"), statics.Arity).ToArray()));
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
