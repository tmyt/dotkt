using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin property accessor identity is an explicit BIR fact.  This is the sole forward projection from that
// identity to the legacy CLR accessor spelling retained for #397; #393 changes this one rule.  No caller may parse
// the resulting method name to recover the property or accessor role.
static class KotlinPropertyAccessors
{
    internal const string SourceNameKey = "propertyName";
    internal const string KindKey = "propertyAccessor";
    internal const string PropertyRolesKey = "kotlinAccessors";
    internal const string AssociationKey = "propertyAssociation";
    // A CLR Property signature cannot own method generic parameters. Generic top-level extension properties therefore
    // carry their exact accessor association on the MethodDef instead of emitting an invalid Property row.
    internal const string MetadataCarrierKey = "kotlinPropertyAccessorCarrier";

    internal static string PhysicalName(string sourceName, string kind) => kind switch
    {
        "get" => "get_" + sourceName,
        "set" => "set_" + sourceName,
        _ => throw new InvalidOperationException($"invalid Kotlin property accessor role '{kind}'"),
    };

    internal static bool TryIdentity(JsonObject declaration, out string sourceName, out string kind)
    {
        sourceName = Str(declaration[SourceNameKey]);
        kind = Str(declaration[KindKey]);
        if (sourceName == null && kind == null) return false;
        if (sourceName == null || kind is not ("get" or "set"))
            throw new InvalidOperationException("incomplete Kotlin property accessor identity");
        return true;
    }

    // A MethodImpl bridge is a resolved CLR implementation detail, not another Kotlin declaration that a frontend-
    // selected property call may bind to.  It deliberately carries the source identity for MethodSemantics and
    // round-trip metadata, so every source-declaration index must exclude it by this explicit physical-role fact.
    internal static bool IsPhysicalSlotBridge(JsonObject declaration) =>
        declaration["clrInterfaceImpls"] is JsonArray { Count: > 0 }
        || declaration["clrBaseImpls"] is JsonArray { Count: > 0 };

    internal static void PreserveCallIdentity(JsonObject call, string sourceName, string kind)
    {
        if (sourceName == null || kind is not ("get" or "set"))
            throw new InvalidOperationException("incomplete Kotlin property call identity");
        call[SourceNameKey] = sourceName;
        call[KindKey] = kind;
    }

    internal static bool IsCall(JsonObject call, string sourceName, string kind) =>
        Str(call[SourceNameKey]) == sourceName && Str(call[KindKey]) == kind
        || Str(call["method"]) == sourceName && Str(call["prop"]) == kind;

    internal static bool TryCallIdentity(JsonObject call, out string sourceName, out string kind)
    {
        sourceName = Str(call[SourceNameKey]);
        kind = Str(call[KindKey]);
        if (sourceName != null || kind != null)
        {
            if (sourceName == null || kind is not ("get" or "set"))
                throw new InvalidOperationException("incomplete preserved Kotlin property call identity");
            return true;
        }
        sourceName = Str(call["method"]);
        kind = Str(call["prop"]);
        if (sourceName != null && kind is "get" or "set") return true;
        sourceName = null;
        kind = null;
        return false;
    }

    internal static void SetPhysicalName(JsonObject declaration, string physicalName)
    {
        if (!TryIdentity(declaration, out _, out _))
            throw new InvalidOperationException("cannot allocate a physical accessor name without Kotlin identity");
        declaration["name"] = physicalName;
    }

    internal static void RemoveIdentity(JsonObject declaration)
    {
        declaration.Remove(SourceNameKey);
        declaration.Remove(KindKey);
        declaration.Remove(AssociationKey);
    }

    public static void AllocateDeclarationsAndProperties(IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) Walk(root, allocateCalls: false, null, null);
    }

    public static void AllocateAll(JsonNode root, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
            IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> localAccessors) =>
        Walk(root, allocateCalls: true, refs, localAccessors);

    static void Walk(JsonNode node, bool allocateCalls, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
            IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> localAccessors)
    {
        if (node is JsonObject obj)
        {
            var propertyAccess = Str(obj["prop"]);
            if (allocateCalls && Str(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall"
                && propertyAccess is "get" or "set"
                && Str(obj["method"]) is string propertyName)
            {
                PreserveCallIdentity(obj, propertyName, propertyAccess);
                var physicalName = PhysicalName(propertyName, propertyAccess);
                // Static reference properties and companion-extension carriers have already consumed their exact
                // metadata/binding before this final pass. A remaining callStatic is an ordinary local top-level or
                // type-owned property and intentionally receives the same forward spelling as its declaration below.
                // Instance calls additionally need the exact local/reference index because an override or bridge can
                // carry a physical name allocated by an earlier representation pass.
                var ownerSlot = Str(obj["k"]) == "constrainedCall" ? obj["iface"] : obj["ownerType"];
                if (Str(obj["k"]) is "callInstance" or "constrainedCall"
                    && TypeJson.Read(ownerSlot) is TypeNode.Fqn ownerType)
                {
                    var owner = ReferenceMetadataIndex.BareOwnerFqn(ownerType.Name);
                    var paramCount = (obj["args"] as JsonArray)?.Count ?? 0;
                    var signatureNode = obj["sig"] as JsonArray ?? obj["argTypes"] as JsonArray;
                    var signature = signatureNode?.Select(TypeJson.Read).ToArray();
                    if (signature != null &&
                        (signature.Length != paramCount || signature.Any(type => type == null)))
                        signature = null;
                    var methodArity = (obj["typeArgs"] as JsonArray)?.Count ?? 0;
                    if (localAccessors != null
                        && MemberCallSubstitution.TryResolveLocalPropertyAccessor(localAccessors,
                            owner, propertyName, propertyAccess, methodArity, paramCount, signature,
                            ownerType.Args ?? Array.Empty<TypeNode>(), out var localName))
                        physicalName = localName;
                    else if (refs != null && refs.TryKotlinPropertyAccessor(owner, propertyName, propertyAccess,
                                 paramCount, methodArity, signature, ownerType.Args ?? Array.Empty<TypeNode>(),
                                 out var referencedName,
                                 out var referencedVirtual))
                    {
                        physicalName = referencedName;
                        // An earlier hierarchy pass may already have proved virtual dispatch through a referenced
                        // ancestor. A direct lookup can add that fact, but must not erase it.
                        if (referencedVirtual) obj["virtual"] = true;
                    }
                }
                obj["method"] = physicalName;
                obj.Remove("prop");
            }
            if (obj["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (TryIdentity(method, out var sourceName, out var kind))
                    {
                        var current = Str(method["name"]);
                        // A representation pass such as #389 may already have allocated a narrower core/container
                        // identity through SetPhysicalName.  Ordinary kotc declarations still carry their semantic
                        // source name here and receive the uniform legacy ABI spelling now.
                        if (current == sourceName) SetPhysicalName(method, PhysicalName(sourceName, kind));
                    }

            if (obj["properties"] is JsonArray properties)
            {
                var ownerMethods = obj["methods"] is JsonArray ownerMethodArray
                    ? ownerMethodArray.OfType<JsonObject>().ToArray()
                    : Array.Empty<JsonObject>();
                var unrepresentable = new List<JsonObject>();
                foreach (var property in properties.OfType<JsonObject>())
                    if (!AllocateProperty(property, ownerMethods, allocateCalls))
                        unrepresentable.Add(property);
                if (allocateCalls)
                    foreach (var property in unrepresentable)
                        properties.Remove(property);
            }

            foreach (var child in obj.Select(pair => pair.Value).ToArray())
                if (child != null) Walk(child, allocateCalls, refs, localAccessors);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child != null) Walk(child, allocateCalls, refs, localAccessors);
    }

    // Returns whether this semantic Kotlin property has a representable CLR Property signature. A MethodDef may own
    // `!!T`; a Property row may not, so a method-generic accessor is exported through the explicit metadata carrier
    // stamped below by RoundtripMetadata and no CLR Property row is emitted.
    static bool AllocateProperty(JsonObject property, IReadOnlyList<JsonObject> methods, bool stripIdentity)
    {
        if (property[PropertyRolesKey] is not JsonArray roles) return true;
        var sourceName = Str(property["name"])
            ?? throw new InvalidOperationException("Kotlin property record has no source name");
        var association = Str(property[AssociationKey])
            ?? throw new InvalidOperationException($"Kotlin property record '{sourceName}' has no accessor association");
        var get = false;
        var set = false;
        var resolved = new List<(JsonObject Method, string Role)>();
        foreach (var roleNode in roles)
        {
            var role = Str(roleNode);
            if (role is not ("get" or "set"))
                throw new InvalidOperationException($"invalid Kotlin property record accessor role '{role}'");
            var matches = methods.Where(method =>
                Str(method[AssociationKey]) == association &&
                Str(method[KindKey]) == role).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Kotlin property record '{sourceName}' association '{association}' resolves " +
                    $"{matches.Length} '{role}' accessor declaration(s)");
            resolved.Add((matches[0], role));
        }
        var representable = resolved.All(item =>
            ((item.Method["typeParams"] as JsonArray)?.Count ?? 0) == 0);
        if (!representable)
            foreach (var (method, role) in resolved)
                method[MetadataCarrierKey] = new JsonObject
                {
                    ["name"] = sourceName,
                    ["kind"] = role,
                    ["association"] = association,
                };

        foreach (var (method, role) in resolved)
        {
            var physicalName = Str(method["name"])
                ?? throw new InvalidOperationException("Kotlin property accessor has no allocated physical name");
            var signature = new JsonArray(
                ((method["params"] as JsonArray) ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(parameter => parameter["type"]?.DeepClone()
                        ?? throw new InvalidOperationException("Kotlin property accessor parameter has no type"))
                    .ToArray());
            var methodArity = (method["typeParams"] as JsonArray)?.Count ?? 0;
            if (role == "get")
            {
                property["get"] = physicalName;
                property["getSig"] = signature;
                property["getMethodArity"] = methodArity;
                get = true;
            }
            else
            {
                property["set"] = physicalName;
                property["setSig"] = signature;
                property["setMethodArity"] = methodArity;
                set = true;
            }
        }
        if (!get && !set)
            throw new InvalidOperationException($"Kotlin property record '{sourceName}' has no accessor roles");
        if (!get)
        {
            property["get"] = null;
            property.Remove("getSig");
            property.Remove("getMethodArity");
        }
        if (!set)
        {
            property["set"] = null;
            property.Remove("setSig");
            property.Remove("setMethodArity");
        }
        if (stripIdentity)
        {
            property.Remove(PropertyRolesKey);
            property.Remove(AssociationKey);
        }
        return representable;
    }

    // Associate a synthesized MethodImpl body with an exact physical Property shape. Both override-bridge passes use
    // this one rule so a bridge cannot lose the semantic accessor fact merely because its return conversion differs.
    internal static void AssociateBridgeProperty(JsonObject owner, JsonObject bridge, string propertyName,
        string accessorKind, string sourceAssociation, TypeNode[] slotParams, TypeNode slotRet)
    {
        if (accessorKind is not ("get" or "set"))
            throw new InvalidOperationException("property override bridge has no getter/setter role");
        if (accessorKind == "set" && slotParams.Length == 0)
            throw new InvalidOperationException("property setter override bridge has no value parameter");
        if (string.IsNullOrEmpty(sourceAssociation))
            throw new InvalidOperationException("property override bridge has no source association");
        var propertyType = accessorKind == "get" ? slotRet : slotParams[^1];
        var indexParams = accessorKind == "get" ? slotParams : slotParams[..^1];
        var association = "dotkt$bridge$property$" + sourceAssociation + "|" +
            SupertypeGraph.TypeKey(propertyType) + "(" +
            string.Join(",", indexParams.Select(SupertypeGraph.TypeKey)) + ")";
        bridge[SourceNameKey] = propertyName;
        bridge[KindKey] = accessorKind;
        bridge[AssociationKey] = association;
        // A constructed generic interface is named by MemberRef in MethodImpl metadata. Keep the exact source role on
        // the local bridge MethodDef as well as in MethodSemantics so reverse projection never needs the declaration's
        // physical name (and remains sound if a metadata reader cannot recover the MemberRef's Property row).
        bridge[MetadataCarrierKey] = new JsonObject
        {
            ["name"] = propertyName,
            ["kind"] = accessorKind,
            ["association"] = association,
        };
        var properties = owner["properties"] as JsonArray;
        if (properties == null)
        {
            properties = new JsonArray();
            owner["properties"] = properties;
        }
        var property = properties.OfType<JsonObject>()
            .SingleOrDefault(candidate => Str(candidate[AssociationKey]) == association);
        if (property == null)
        {
            properties.Add(new JsonObject
            {
                ["name"] = propertyName,
                ["type"] = TypeJson.Write(propertyType),
                [PropertyRolesKey] = new JsonArray(accessorKind),
                [AssociationKey] = association,
            });
            return;
        }
        if (Str(property["name"]) != propertyName
            || TypeJson.Read(property["type"]) is not TypeNode existingType
            || !existingType.Equals(propertyType)
            || property[PropertyRolesKey] is not JsonArray propertyRoles
            || propertyRoles.Any(role => Str(role) == accessorKind))
            throw new InvalidOperationException("property override bridges do not form one unique CLR Property shape");
        propertyRoles.Add(accessorKind);
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
