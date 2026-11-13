using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin companion-block static on G<T> is one logical declaration, while CLR static storage and .cctors on G<T>
// exist once per constructed type. Put the declarations on one non-generic compiler carrier and bind every use to it.
// The trusted carrier is also consumed by dll2klib, which merges those declarations back into semantic G rather than
// exposing the physical implementation class. No representative type argument or generic-constraint guess exists.
static partial class GenericStaticOwnerBinding
{
    internal const string Marker = "$dotkt_statics";
    const string CarrierInitMarker = "dotkt$initialized";
    const string OwnerInitTrigger = "dotkt$staticInit";

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var all = roots.ToArray();
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var localGenericOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in all)
        {
            CollectCarriers(root, local);
            CollectGenericOwners(root, localGenericOwners);
        }
        foreach (var root in all)
            Walk(root, local, localGenericOwners, refs);
    }

    static void CollectGenericOwners(JsonNode node, HashSet<string> owners)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (TypeParameterFrame.Count(type) > 0 && Str(type["name"]) is string name) owners.Add(name);
            CollectGenericOwners(type, owners);
        }
    }

    static void CollectCarriers(JsonNode node, Dictionary<string, string> local)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (type["staticCarrier"] is JsonObject carrier &&
                Str(carrier["owner"]) is string owner && Str(type["name"]) is string physical)
                local.Add(owner, physical);
            CollectCarriers(type, local);
        }
    }

    static void Walk(JsonNode node, IReadOnlyDictionary<string, string> local,
        IReadOnlySet<string> localGenericOwners, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                Bind(obj, local, localGenericOwners, refs);
                foreach (var child in obj.ToArray())
                    if (child.Value != null) Walk(child.Value, local, localGenericOwners, refs);
                break;
            case JsonArray array:
                foreach (var child in array.ToArray())
                    if (child != null) Walk(child, local, localGenericOwners, refs);
                break;
        }
    }

    static void Bind(JsonObject node, IReadOnlyDictionary<string, string> local,
        IReadOnlySet<string> localGenericOwners, ReferenceMetadataIndex refs)
    {
        var kind = Str(node["k"]);
        var keys = kind switch {
            "staticField" or "staticFieldSet" or "setStaticField" or "setStaticFieldExpr" => new[] { "ownerType" },
            "lateinitGet" when Bool(node["static"]) => new[] { "ownerType" },
            "clrEventGet" or "clrEventRaise" when Bool(node["static"]) => new[] { "type" },
            "callStatic" => new[] { "ownerType", "owner" },
            _ => [],
        };
        string selectedCarrier = null;
        foreach (var key in keys)
        {
            var carrier = SelectCarrier(TypeJson.Read(node[key]), kind, local, localGenericOwners, refs);
            if (carrier == null) continue;
            node[key] = TypeJson.Write(new TypeNode.Fqn(carrier));
            selectedCarrier = carrier;
        }
        // The semantic owner's type-parameter frame is a frontend declaration fact. Once this pass selects the
        // explicit non-generic physical carrier, any retained lexical-access descriptor must describe that physical
        // owner too; UnsafeAccessor must not re-declare the generic owner's unrelated T on a carrier access.
        if (selectedCarrier != null && node.ContainsKey("memberOwnerTypeParams"))
            node["memberOwnerTypeParams"] = new JsonArray();
        if (kind == "callStatic" && selectedCarrier != null)
        {
            // This late pass has selected an exact physical MethodDef owner. Move it onto CIR's dispatch axis just
            // like LocalStaticOwnerBinding does; leaving ownerType would still look like an unresolved substitution
            // call to ilemit and would incorrectly require a file-facade calleeOwner.
            node["owner"] = TypeJson.Write(new TypeNode.Fqn(selectedCarrier));
            node.Remove("ownerType");
        }
    }

    static string SelectCarrier(TypeNode type, string kind, IReadOnlyDictionary<string, string> local,
        IReadOnlySet<string> localGenericOwners, ReferenceMetadataIndex refs)
    {
        // A constructed owner already states an exact CLR frame; only the
        // receiverless Kotlin static surface selects a non-generic carrier.
        if (type is not TypeNode.Fqn owner || owner.Args is { Length: > 0 }) return null;
        var bare = owner.Name;
        if (!local.TryGetValue(bare, out var carrier) && !refs.TryGenericStaticCarrier(bare, out carrier))
        {
            if (!local.Values.Contains(bare, StringComparer.Ordinal) && !refs.IsGenericStaticCarrier(bare))
            {
                // Native event owners are closed later by NetInteropBinding.
                if (kind is "clrEventGet" or "clrEventRaise" && !localGenericOwners.Contains(bare) && !refs.HasDotKtOwner(bare)) return null;
                if (owner.Args == null && (localGenericOwners.Contains(bare) || refs.OwnerArity(bare) > 0))
                    throw new InvalidOperationException($"static access to generic owner '{bare}' has no constructed type or explicit non-generic carrier");
                return null;
            }
            carrier = bare;
        }
        return carrier;
    }

    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
