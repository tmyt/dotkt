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

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var index = new Dictionary<string, GenericStatics>(StringComparer.Ordinal);
        foreach (var root in rootList) Collect(root, index);
        foreach (var root in rootList) Walk(root, index, refs);
    }

    static void Collect(JsonNode node, Dictionary<string, GenericStatics> index)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var item in types.OfType<JsonObject>())
        {
            var name = Str(item["name"]);
            var arity = TypeParameterFrame.Count(item);
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

    static void Walk(JsonNode node, Dictionary<string, GenericStatics> index, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                Bind(obj, index, refs);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, index, refs);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item, index, refs);
                break;
        }
    }

    static void Bind(JsonObject node, Dictionary<string, GenericStatics> index, ReferenceMetadataIndex refs)
    {
        var kind = Str(node["k"]);
        string[] ownerKeys;
        bool isField;
        switch (kind)
        {
            case "staticField":
            case "staticFieldSet":
            case "setStaticField":
            case "setStaticFieldExpr":
                ownerKeys = ["ownerType"];
                isField = true;
                break;
            case "callStatic":
                // BOTH owner axes, whenever both are present. An earlier pass may have moved the declaring type onto
                // the CLR `owner` axis while `ownerType` still carries the Kotlin identity; closing only one leaves
                // the other spelling the OPEN generic definition, which is not a legal MemberRef parent — and ilemit
                // dispatches from `owner`, so the stale one is exactly the one that reaches the emitted IL.
                ownerKeys = ["ownerType", "owner"];
                isField = false;
                break;
            default:
                return;
        }

        var member = Str(node[isField ? "name" : "method"]);
        if (member == null) return;
        foreach (var ownerKey in ownerKeys)
        {
            if (TypeJson.Read(node[ownerKey]) is not TypeNode.Fqn { Args: null } owner) continue;
            int arity;
            if (index.TryGetValue(owner.Name, out var statics))
            {
                if (!(isField ? statics.Fields.Contains(member) : statics.Methods.Contains(member))) continue;
                arity = statics.Arity;
            }
            else
            {
                // A referenced declaration is absent from the local type index. Its static node kind is already the BIR
                // semantic fact; use only the referenced owner's generic arity to select the required CLR TypeSpec.
                arity = refs.OwnerArity(owner.Name);
                if (arity == 0) continue;
            }
            node[ownerKey] = TypeJson.Write(new TypeNode.Fqn(owner.Name,
                Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Any"), arity).ToArray()));
        }
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
