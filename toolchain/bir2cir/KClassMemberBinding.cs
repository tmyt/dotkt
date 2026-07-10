using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A `kotlin.reflect.KClass` member read (`T::class.simpleName` / `.qualifiedName`) -> its System.Type BCL member.
// kotc emits the pure-Kotlin property read `callInstance(kotlin.reflect.KClass[..].get_simpleName, recv = <::class>)`;
// the `::class` receiver is already a System.Type token (a `getType`/`classRef` node), and KClass is @ClrTypeAlias-ed
// onto System.Type, so the member binds to Type.Name / Type.FullName. This is the Kotlin<->CLR relation, so the
// System.Type / BCL-member knowledge lives here (not in kotc). Mirrors ClrEventOperatorBinding's bottom-up rewrite.
static class KClassMemberBinding
{
    public static JsonNode Apply(JsonNode root) => Walk(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonNode Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj) copy[kv.Key] = kv.Value == null ? null : Walk(kv.Value);   // children first (bottom-up)
            return Transform(copy) ?? copy;
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Walk(item));
            return copy;
        }
        return node.DeepClone();
    }

    static JsonNode Transform(JsonObject node)
    {
        if (Str(node["k"]) != "callInstance") return null;
        // ownerType is `kotlin.reflect.KClass` (its type-arg, if any, is dropped by OwnerName — we key on the identity).
        if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.reflect.KClass") return null;
        var bcl = Str(node["method"]) switch
        {
            "get_simpleName" => "Name",
            "get_qualifiedName" => "FullName",
            _ => null,
        };
        if (bcl == null) return null;
        if (node["recv"] is not JsonObject recv) return null;   // the ::class receiver (a System.Type value)
        return new JsonObject
        {
            ["k"] = "clrPropGet",
            ["type"] = TypeJson.Fqn("System.Type"),
            ["name"] = bcl,
            ["ret"] = TypeJson.Fqn("System.String"),
            ["static"] = false,
            ["recv"] = recv.DeepClone(),
        };
    }
}

