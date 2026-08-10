using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
            var removedAccessors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (var i = methods.Count - 1; i >= 0; i--)
            {
                if (methods[i] is not JsonObject method || !Bool(method["fakeOverride"])
                    || method["body"] is not JsonArray body || body.Count != 0
                    || method["overrides"] is not JsonArray overrides) continue;
                if (!HasConcreteAncestor(method, overrides, local, refs)) continue;
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

    static bool HasConcreteAncestor(JsonObject fakeOverride, JsonArray overrides,
        IReadOnlyDictionary<string, JsonObject> local, ReferenceMetadataIndex refs)
    {
        var signature = (fakeOverride["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(parameter => TypeJson.Read(parameter["type"]))
            .ToArray();
        if (signature?.Any(type => type == null) == true) signature = null;
        var methodArity = (fakeOverride["typeParams"] as JsonArray)?.Count ?? 0;
        foreach (var node in overrides.OfType<JsonObject>())
        {
            var ownerType = TypeJson.Read(node["owner"]) as TypeNode.Fqn;
            var owner = ownerType?.Name ?? TypeJson.OwnerName(node["owner"]);
            var member = Str(node["member"]);
            var kind = Str(node["kind"]);
            var paramCount = Int(node["arity"]);
            if (owner == null || member == null) continue;
            // A Kotlin ancestor whose identity is replaced by @ClrTypeAlias cannot supply its Kotlin-named
            // default slot in the emitted hierarchy.  Reference assemblies strip bodies with concrete throw
            // stubs, so MethodInfo.IsAbstract alone would otherwise misclassify Collection.iterator (and any
            // equivalent aliased API) as an inherited DIM even though the CLR alias has no such member.
            if (refs.Aliases.ContainsKey(owner)) continue;
            var accessorKind = kind switch { "getter" => "get", "setter" => "set", _ => null };
            if (local.TryGetValue(owner, out var declaration)
                && declaration["methods"] is JsonArray methods)
            {
                var candidates = methods.OfType<JsonObject>().Where(m => (accessorKind != null
                        ? KotlinPropertyAccessors.TryIdentity(m, out var propertyName, out var propertyKind)
                            && propertyName == member && propertyKind == accessorKind
                        : Str(m["name"]) == member && !KotlinPropertyAccessors.TryIdentity(m, out _, out _))
                    && (m["params"] as JsonArray)?.Count == paramCount
                    && ((m["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                    && m["body"] is JsonArray b && b.Count != 0
                    && SignatureMatches(m, signature, ownerType?.Args)).ToList();
                if (candidates.Count == 1) return true;
            }
            if (accessorKind != null
                ? refs.DeclaresConcretePropertyAccessor(owner, member, accessorKind, paramCount,
                    methodArity, signature, ownerType?.Args ?? Array.Empty<TypeNode>())
                : refs.DeclaresConcreteMember(owner, member, paramCount)) return true;
        }
        return false;
    }

    static bool SignatureMatches(JsonObject declaration, IReadOnlyList<TypeNode> signature,
        TypeNode[] ownerTypeArguments)
    {
        if (signature == null) return true;
        if (declaration["params"] is not JsonArray parameters || parameters.Count != signature.Count) return false;
        for (var i = 0; i < signature.Count; i++)
        {
            if (parameters[i] is not JsonObject parameter
                || TypeJson.Read(parameter["type"]) is not TypeNode declared) return false;
            if (ownerTypeArguments != null)
                declared = SupertypeGraph.SubstOwnerTvs(declared, ownerTypeArguments);
            if (!ReferenceMetadataIndex.AccessorDeclarationDescribesCall(declared, signature[i])) return false;
        }
        return true;
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static int Int(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<int>(out var result) ? result : -1;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
