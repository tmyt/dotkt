using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

// Kotlin IR materializes inherited interface members as fake overrides. The frontend has already selected whether such
// a member inherits a concrete implementation and records that answer as "inheritedImplementation". Consuming that
// explicit fact here avoids shadowing a DIM with a fresh abstract CLR slot. bir2cir must not rediscover the answer from
// ancestor bodies, CLR metadata, or a physical method name.
static class InheritedDefaultFakeOverrideElision
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject ro || ro["types"] is not JsonArray types) return;
        var local = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        CollectTypes(types, local);
        foreach (var type in local.Values)
        {
            if (Str(type["kind"]) != "interface" || type["methods"] is not JsonArray methods) continue;
            var removedAccessors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (var i = methods.Count - 1; i >= 0; i--)
            {
                if (methods[i] is not JsonObject method || !Bool(method["fakeOverride"])
                    || method[KotlinPropertyAccessors.InheritedImplementationKey] is not JsonObject) continue;
                if (KotlinPropertyAccessors.TryIdentity(method, out _, out var accessorKind)
                    && Str(method[KotlinPropertyAccessors.AssociationKey]) is string association)
                {
                    if (!removedAccessors.TryGetValue(association, out var roles))
                        removedAccessors[association] = roles = new HashSet<string>(StringComparer.Ordinal);
                    roles.Add(accessorKind);
                }
                methods.RemoveAt(i);
            }
            if (removedAccessors.Count != 0 && type["properties"] is JsonArray properties)
                for (var i = properties.Count - 1; i >= 0; i--)
                    if (properties[i] is JsonObject property
                        && Str(property[KotlinPropertyAccessors.AssociationKey]) is string association
                        && removedAccessors.TryGetValue(association, out var removedRoles)
                        && property[KotlinPropertyAccessors.PropertyRolesKey] is JsonArray roles)
                    {
                        for (var roleIndex = roles.Count - 1; roleIndex >= 0; roleIndex--)
                            if (removedRoles.Contains(Str(roles[roleIndex]))) roles.RemoveAt(roleIndex);
                        if (roles.Count == 0) properties.RemoveAt(i);
                    }
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

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
