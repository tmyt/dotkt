using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// Kotlin IR materializes inherited interface members as fake overrides.  A fake override whose declaration is already
// supplied by a default interface method is not a new CLR slot: emitting it as an abstract method shadows the inherited
// DIM and makes every concrete implementer require a forwarding MethodImpl.  Keep the fact in BIR, then consume it here
// by removing only positively identified fake overrides backed by a concrete ancestor declaration.
//
// This is hierarchy/metadata driven.  It contains no Kotlin library, owner, or member-name special cases.  A genuine
// source declaration is never removed, nor is a fake override whose ancestor remains abstract.
static class InheritedDefaultFakeOverrideElision
{
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject ro || ro["types"] is not JsonArray types) return;
        var local = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        CollectTypes(types, local);
        foreach (var type in local.Values)
        {
            if (Str(type["kind"]) != "interface" || type["methods"] is not JsonArray methods) continue;
            var removed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = methods.Count - 1; i >= 0; i--)
            {
                if (methods[i] is not JsonObject method || !Bool(method["fakeOverride"])
                    || method["body"] is not JsonArray body || body.Count != 0
                    || method["overrides"] is not JsonArray overrides) continue;
                if (!HasConcreteAncestor(overrides, local, refs)) continue;
                if (Str(method["name"]) is string name) removed.Add(name);
                methods.RemoveAt(i);
            }
            if (removed.Count != 0 && type["properties"] is JsonArray properties)
                for (var i = properties.Count - 1; i >= 0; i--)
                    if (properties[i] is JsonObject property
                        && Str(property["get"]) is string getter && removed.Contains(getter))
                        properties.RemoveAt(i);
        }
    }

    static void CollectTypes(JsonArray types, Dictionary<string, JsonObject> result)
    {
        foreach (var node in types.OfType<JsonObject>())
        {
            if (Str(node["name"]) is string name) result[name] = node;
            if (node["types"] is JsonArray nested) CollectTypes(nested, result);
        }
    }

    static bool HasConcreteAncestor(JsonArray overrides, IReadOnlyDictionary<string, JsonObject> local,
        ReferenceMetadataIndex refs)
    {
        foreach (var node in overrides.OfType<JsonObject>())
        {
            var owner = TypeJson.OwnerName(node["owner"]);
            var member = Str(node["member"]);
            var kind = Str(node["kind"]);
            var paramCount = Int(node["arity"]);
            if (owner == null || member == null) continue;
            // A Kotlin ancestor whose identity is replaced by @ClrTypeAlias cannot supply its Kotlin-named
            // default slot in the emitted hierarchy.  Reference assemblies strip bodies with concrete throw
            // stubs, so MethodInfo.IsAbstract alone would otherwise misclassify Collection.iterator (and any
            // equivalent aliased API) as an inherited DIM even though the CLR alias has no such member.
            if (refs.Aliases.ContainsKey(owner)) continue;
            var clrName = kind switch
            {
                "getter" => "get_" + member,
                "setter" => "set_" + member,
                _ => member,
            };
            if (local.TryGetValue(owner, out var declaration)
                && declaration["methods"] is JsonArray methods
                && methods.OfType<JsonObject>().Any(m => Str(m["name"]) == clrName
                    && (m["params"] as JsonArray)?.Count == paramCount
                    && m["body"] is JsonArray b && b.Count != 0))
                return true;
            if (refs.DeclaresConcreteMember(owner, clrName, paramCount)) return true;
        }
        return false;
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static int Int(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<int>(out var result) ? result : -1;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
