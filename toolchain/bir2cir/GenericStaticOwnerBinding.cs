using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A Kotlin companion-block static on G<T> is one logical declaration, while CLR static storage and .cctors on G<T>
// exist once per constructed type. Put the declarations on one non-generic compiler carrier and bind every use to it.
// The trusted carrier is also consumed by dll2klib, which merges those declarations back into semantic G rather than
// exposing the physical implementation class. No representative type argument or generic-constraint guess exists.
static class GenericStaticOwnerBinding
{
    internal const string Marker = "$dotkt_statics";
    const string CarrierInitMarker = "dotkt$initialized";
    const string OwnerInitTrigger = "dotkt$staticInit";

    public static void Materialize(IEnumerable<JsonNode> roots)
    {
        var rootObjects = roots.OfType<JsonObject>().ToArray();
        // TypeDefs share one module-wide CLR namespace even though kotc emits one BIR root per source file. Reserve
        // carrier identities across every root before mutating any of them, so a user declaration in a sibling file
        // cannot collide with the generated implementation type.
        var names = rootObjects
            .SelectMany(root => (root["types"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(type => Str(type["name"]))
            .Where(name => name != null)
            .ToHashSet(StringComparer.Ordinal);
        var carrierOwners = rootObjects
            .SelectMany(root => (root["types"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(type => TypeParameterFrame.Count(type) > 0 &&
                (((type["fields"] as JsonArray)?.OfType<JsonObject>()
                    .Any(field => Bool(field["static"]) && Bool(field["kotlinStatic"])) ?? false) ||
                 ((type["methods"] as JsonArray)?.OfType<JsonObject>()
                    .Any(method => Bool(method["static"]) && Bool(method["kotlinStatic"])) ?? false)))
            .Select(type => Str(type["name"]))
            .Where(name => name != null)
            .ToHashSet(StringComparer.Ordinal);

        // kotc keeps non-capturing lambda helpers in the file method table and links them back to their lexical
        // Kotlin owner with semanticOwner/newDelegate.calleeOwner. If the enclosing declaration is moved to a
        // non-generic static carrier, every generated helper reachable from it belongs to the same physical owner.
        // Index the explicit declaration links before mutating any root; no generated-name convention is inferred.
        var generatedMethods = new Dictionary<(string Owner, string Name),
            (JsonObject Method, JsonArray Container)>();
        foreach (var root in rootObjects)
        {
            if (root["methods"] is not JsonArray rootMethods) continue;
            foreach (var method in rootMethods.OfType<JsonObject>())
            {
                if (!Bool(method["generated"]) || Str(method["semanticOwner"]) is not string semanticOwner
                    || !carrierOwners.Contains(semanticOwner)
                    || Str(method["name"]) is not string methodName)
                    continue;
                if (!generatedMethods.TryAdd((semanticOwner, methodName), (method, rootMethods)))
                    throw new InvalidOperationException(
                        $"generated method '{semanticOwner}.{methodName}' has multiple declarations");
            }
        }
        foreach (var root in rootObjects)
        {
            if (root["types"] is not JsonArray types) continue;
            var declarations = types.OfType<JsonObject>().ToArray();
            foreach (var owner in declarations)
            {
                var ownerName = Str(owner["name"]);
                if (ownerName == null || TypeParameterFrame.Count(owner) == 0) continue;
                var fields = (owner["fields"] as JsonArray)?.OfType<JsonObject>()
                    .Where(f => Bool(f["static"]) && Bool(f["kotlinStatic"])).ToArray() ?? [];
                var methods = (owner["methods"] as JsonArray)?.OfType<JsonObject>()
                    .Where(m => Bool(m["static"]) && Bool(m["kotlinStatic"])).ToArray() ?? [];
                if (fields.Length == 0 && methods.Length == 0) continue;
                var hasRuntimeInitializer = fields.Any(field => !Bool(field["const"]) && field["init"] != null);

                var carrierName = ownerName + Marker;
                if (!names.Add(carrierName))
                    throw new InvalidOperationException($"reserved generic-static carrier '{carrierName}' is already declared");

                // A lifted local/anonymous type inside a Kotlin-static member is semantically owned by G, but cannot
                // capture G's T. kotc states that fact explicitly; now that this pass has selected the non-generic
                // carrier, rehome the implementation type before TypeOwnershipLowering chooses CLR nesting.
                foreach (var implementation in rootObjects
                    .SelectMany(candidate => (candidate["types"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    .Where(type => Str(type["staticSemanticOwner"]) == ownerName))
                {
                    if (Str(implementation["semanticOwner"]) != ownerName)
                        throw new InvalidOperationException(
                            $"Kotlin-static implementation type '{Str(implementation["name"])}' has inconsistent semantic owner");
                    implementation["semanticOwner"] = carrierName;
                    implementation.Remove("staticSemanticOwner");
                }

                // A moved member can create lambdas/closures. Relocate every explicitly-linked non-capturing helper
                // into the carrier, and retarget synthetic ownership facts under the moved subtree so later closure/
                // coroutine synthesis nests its implementation types under the carrier rather than open G<T>.
                var liftedHelpers = CollectLiftedHelpers(
                    fields.Cast<JsonNode>().Concat(methods), ownerName, carrierName, methods, generatedMethods);
                var physicalMethods = methods.Concat(liftedHelpers).ToArray();
                foreach (var helper in liftedHelpers)
                {
                    helper.Remove("semanticOwner");
                    var helperName = Str(helper["name"])
                        ?? throw new InvalidOperationException("generated static helper has no name");
                    generatedMethods[(ownerName, helperName)].Container.Remove(helper);
                }

                // A companion-block declaration cannot capture its enclosing class's T. Refuse a malformed frontend
                // projection rather than hoisting a declaration whose physical signature/body still needs that frame.
                foreach (var member in fields.Cast<JsonNode>().Concat(physicalMethods))
                    if (ContainsOwnerTypeVariable(member))
                        throw new InvalidOperationException(
                            $"static member on generic owner '{ownerName}' captures an enclosing type parameter");

                var properties = (owner["properties"] as JsonArray)?.OfType<JsonObject>()
                    .Where(p => Bool(p["kotlinStatic"]))
                    .ToArray() ?? [];
                var physicalFields = fields.ToList();
                if (hasRuntimeInitializer)
                {
                    // CLR initializes a generic TypeDef once per closed instantiation, while Kotlin initializes this
                    // logical static surface once when either the owner or a static member is first used. The source
                    // initializers live on the non-generic carrier. A private sentinel initializer on G<T> reads one
                    // internal carrier marker, so constructing any G<T> triggers the carrier .cctor; later closed G<U>
                    // initializations only reread the already-initialized marker and cannot repeat source side effects.
                    physicalFields.Add(StaticIntField(CarrierInitMarker, "internal",
                        new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = 0 }));
                }

                foreach (var member in fields.Cast<JsonObject>().Concat(physicalMethods)) member.Remove("kotlinStatic");

                Remove(owner["fields"] as JsonArray, fields);
                Remove(owner["methods"] as JsonArray, methods);
                Remove(owner["properties"] as JsonArray, properties);
                if (hasRuntimeInitializer)
                {
                    var ownerFields = owner["fields"] as JsonArray ?? new JsonArray();
                    ownerFields.Add(StaticIntField(OwnerInitTrigger, "private", new JsonObject {
                        ["k"] = "staticField",
                        ["ownerType"] = TypeJson.Fqn(carrierName),
                        ["name"] = CarrierInitMarker,
                    }));
                    owner["fields"] = ownerFields;
                }

                // The carrier is public so another emitted assembly can name its public declarations. Individual
                // members retain their Kotlin visibility; private backing storage remains reachable by its co-located
                // public accessor and never has to be widened.
                var carrier = new JsonObject {
                    ["name"] = carrierName,
                    ["kind"] = "class",
                    ["abstract"] = true,
                    ["final"] = true,
                    ["vis"] = "public",
                    ["generated"] = true,
                    ["base"] = null,
                    ["interfaces"] = new JsonArray(),
                    ["fields"] = Array(physicalFields),
                    ["ctors"] = new JsonArray(),
                    ["methods"] = Array(physicalMethods),
                    ["properties"] = Array(properties),
                    ["attrs"] = new JsonArray(),
                    ["staticCarrier"] = new JsonObject { ["owner"] = ownerName },
                };
                types.Add(carrier);
            }
        }
        foreach (var root in rootObjects) StripMarkers(root);
    }

    static JsonObject StaticIntField(string name, string visibility, JsonObject initializer) => new() {
        ["name"] = name,
        ["type"] = TypeJson.Fqn("kotlin.Int"),
        ["static"] = true,
        ["readOnly"] = true,
        ["initOnly"] = true,
        ["vis"] = visibility,
        ["init"] = initializer,
    };

    static JsonObject[] CollectLiftedHelpers(
        IEnumerable<JsonNode> roots,
        string semanticOwner,
        string physicalOwner,
        IReadOnlyList<JsonObject> movedMethods,
        IReadOnlyDictionary<(string Owner, string Name), (JsonObject Method, JsonArray Container)> generatedMethods)
    {
        var physicalMethodNames = movedMethods.Select(method => Str(method["name"]))
            .Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var helpers = new List<JsonObject>();
        var helperNames = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                // semanticOwner is a representation-neutral lexical fact. Once this pass selects the carrier for the
                // enclosing static declaration, downstream synthesized declarations must receive that physical owner.
                if (Str(obj["semanticOwner"]) == semanticOwner)
                    obj["semanticOwner"] = physicalOwner;

                if (Str(obj["k"]) == "newDelegate"
                    && TypeJson.Read(obj["calleeOwner"]) is TypeNode.Fqn targetOwner
                    && targetOwner.Name == semanticOwner && Str(obj["method"]) is string targetName)
                {
                    if (generatedMethods.TryGetValue((semanticOwner, targetName), out var target))
                    {
                        if (physicalMethodNames.Contains(targetName))
                            throw new InvalidOperationException(
                                $"generated static helper '{semanticOwner}.{targetName}' collides with a moved source declaration");
                        physicalMethodNames.Add(targetName);
                        if (helperNames.Add(targetName))
                        {
                            helpers.Add(target.Method);
                            Walk(target.Method);
                        }
                    }
                    else if (!physicalMethodNames.Contains(targetName))
                        throw new InvalidOperationException(
                            $"static member on generic owner '{semanticOwner}' has an unresolved delegate target '{targetName}'");
                    obj["calleeOwner"] = TypeJson.Fqn(physicalOwner);
                }

                foreach (var child in obj.ToArray())
                    if (child.Value != null) Walk(child.Value);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray()) if (child != null) Walk(child);
        }

        foreach (var root in roots) Walk(root);
        return helpers.ToArray();
    }

    static void StripMarkers(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("kotlinStatic");
            obj.Remove("staticSemanticOwner");
            foreach (var child in obj.ToArray()) if (child.Value != null) StripMarkers(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray()) if (child != null) StripMarkers(child);
    }

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
            "callStatic" => new[] { "ownerType", "owner" },
            _ => [],
        };
        string selectedCarrier = null;
        foreach (var key in keys)
        {
            if (TypeJson.Read(node[key]) is not TypeNode.Fqn owner) continue;
            var bare = owner.Name;
            if (!local.TryGetValue(bare, out var carrier) && !refs.TryGenericStaticCarrier(bare, out carrier))
            {
                if (!local.Values.Contains(bare, StringComparer.Ordinal) && !refs.IsGenericStaticCarrier(bare))
                {
                    if (owner.Args == null &&
                        (localGenericOwners.Contains(bare) || refs.OwnerArity(bare) > 0))
                        throw new InvalidOperationException(
                            $"static access to generic owner '{bare}' has no constructed type or explicit non-generic carrier");
                    continue;
                }
                carrier = bare;
            }
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

    static bool ContainsOwnerTypeVariable(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["t"]) == "tv" && Str(obj["scope"]) == "type") return true;
            // A private/protected access descriptor carries the TARGET owner's complete declaration frame so
            // UnsafeAccessorLowering can author an exact signature later.  That frame can be F-bounded
            // (`T : Comparable<T>`), but it is metadata about the accessed declaration, not a use of the moved
            // companion member's lexical T.  GenericStaticOwnerBinding.Bind clears the frame after selecting the
            // non-generic carrier; excluding it here keeps the genuine signature/body capture check strict.
            return obj.Any(kv => kv.Key != "memberOwnerTypeParams" && kv.Value != null &&
                ContainsOwnerTypeVariable(kv.Value));
        }
        return node is JsonArray array && array.Any(child => child != null && ContainsOwnerTypeVariable(child));
    }

    static void Remove(JsonArray array, IEnumerable<JsonObject> members)
    {
        if (array == null) return;
        foreach (var member in members) array.Remove(member);
    }

    static JsonArray Array(IEnumerable<JsonObject> nodes)
    {
        var result = new JsonArray();
        foreach (var node in nodes) result.Add(node);
        return result;
    }

    static bool Bool(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
