using System.Text.Json.Nodes;
using DotKt.Bir;

// Consumes kotc's representation-neutral companion declarations before any ordinary BIR pass sees them.
// Every companion becomes one compiler-named ordinary CLR class carrying exactly one `$INSTANCE`, so the Kotlin
// declaration and its singleton stay one-to-one. CLR static storage belongs to each closed constructed generic type,
// so a carrier owned by a generic type would multiply that singleton per instantiation; a companion whose physical
// owner has ANY generic slot is therefore HOISTED out of it to a non-generic top-level sidecar and the owner keeps
// only a source-named accessor field pointing at it. A non-generic owner keeps its nested carrier, which is already
// one closed type. Neither shape has generic captures, so no companion carries type parameters at all.
// No semantic companion node reaches CIR.
static class CompanionRepresentationLowering
{
    // Separates a hoisted carrier's flattened owner path from the companion's source name. See Apply below for why a
    // bare `$` is not enough.
    internal const string HoistedMarker = "$companion$";

    internal sealed record Association(
        JsonObject Owner, JsonObject Companion, string OwnerName, string KotlinOwnerName, string SemanticName,
        string SourceName, string Visibility, string PhysicalName, string PhysicalOwner, int PhysicalOwnerArity)
    {
        // Hoisted exactly when the physical owner has generic slots: only then would a nested carrier acquire them,
        // and with them one singleton per closed owner.
        internal bool Hoisted => PhysicalOwnerArity > 0;
    }
    public sealed class LocalIndex
    {
        internal LocalIndex(IReadOnlyList<Association> associations) => Associations = associations;
        internal IReadOnlyList<Association> Associations { get; }
    }

    public static LocalIndex Apply(IEnumerable<JsonNode> roots)
    {
        var rootObjects = roots.OfType<JsonObject>().ToArray();
        var types = rootObjects
            .SelectMany(r => (r["types"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .ToList();
        var byName = types.ToDictionary(t => Str(t["name"]), StringComparer.Ordinal);
        var associations = new List<Association>();
        foreach (var companion in types.Where(t => t["kotlinCompanion"] is JsonObject fact &&
                     Str(t["name"]) != Str(fact["owner"])).ToArray())
        {
            var fact = (JsonObject)companion["kotlinCompanion"]!;
            var ownerName = Required(fact, "owner");
            var sourceName = Required(fact, "name");
            var visibility = Required(fact, "visibility");
            var semanticName = Required(companion, "name");
            const string semanticSuffix = ".<companion:";
            var suffixStart = semanticName.LastIndexOf(semanticSuffix, StringComparison.Ordinal);
            if (suffixStart < 0)
                throw new InvalidOperationException($"semantic companion '{semanticName}' has no declaration owner identity");
            var declarationOwnerName = semanticName[..suffixStart];
            if (!byName.TryGetValue(declarationOwnerName, out var owner))
                throw new InvalidOperationException($"semantic companion '{ownerName}.{sourceName}' has no owner declaration");
            // `$` cannot be written in a Kotlin identifier, including a backticked one. Both physical spellings are
            // thus intentionally unspeakable from Kotlin; the source name is restored only from the explicit carrier
            // metadata, never inferred from this physical spelling.
            // A generic physical owner cannot host a non-generic nested TypeDef — CLR nesting redeclares every
            // enclosing slot — so the carrier leaves the owner entirely and flattens the owner's nesting path into
            // its own top-level name. That name shares a namespace with other compiler types derived from an owner,
            // notably the star-projection existential `<owner>$dotkt_star`, and a companion's SOURCE NAME is an
            // ordinary identifier that may be spelled `dotkt_star` too. The reserved `$companion$` marker is what
            // keeps the two apart: every such generated name is an owner followed by ONE `$`-segment, and no Kotlin
            // owner name can end in `$companion`, so no source can produce a colliding pair.
            var physicalOwner = PhysicalMetadataName(owner, byName);
            // The owner's CLR arity counts the slots it captured from an enclosing generic type as well as its own:
            // that is the arity a consumer reflects off the emitted TypeDef, and the arity the carrier metadata is
            // resolved against.
            var physicalOwnerArity = PhysicalTypeParamCount(owner);
            var physicalName = physicalOwnerArity > 0
                ? physicalOwner.Replace('+', '$') + HoistedMarker + sourceName
                : declarationOwnerName + ".$" + sourceName;
            if (types.Any(t => !ReferenceEquals(t, companion) && Str(t["name"]) == physicalName))
                throw new InvalidOperationException(
                    $"reserved physical companion type identity '{physicalName}' is already declared");
            associations.Add(new(owner, companion, declarationOwnerName, ownerName, semanticName,
                sourceName, visibility, physicalName, physicalOwner, physicalOwnerArity));
            owner.Remove("kotlinCompanion");
        }

        foreach (var association in associations)
            Materialize(association);
        foreach (var root in rootObjects)
            RewriteUses(root, associations);
        return new LocalIndex(associations);
    }

    // Run after every inline/default splice and immediately before call-evaluation plans materialize their bindings.
    // Local declarations and validated reference metadata use the same nested-carrier rule.
    public static void BindUses(JsonNode root, LocalIndex local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        RewriteMarkedCaptures(root, local.Associations, refs, bindExternal);
        RewriteUses(root, local.Associations);
        if (bindExternal) BindExternalUses(root, refs);
    }

    // A ProjectReference KLIB preserves the semantic companion owner on callInline, while the trusted inline payload
    // is indexed by its selected CLR declaration owner. Resolve only that declaration identity here, before the splice;
    // value binding remains the later BindUses decision.
    public static void BindSpliceUses(JsonNode root, ReferenceMetadataIndex refs)
    {
        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "callInline" &&
                    obj["owner"] is JsonObject owner && Str(owner["name"]) is string semanticOwner &&
                    refs.TryCompanionPhysicalOwner(semanticOwner, out var physicalOwner))
                    owner["name"] = physicalOwner;
                foreach (var child in obj.ToArray()) if (child.Value is not null) Visit(child.Value);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray()) if (child is not null) Visit(child);
        }
        Visit(root);
    }

    // Every companion carrier — nested or hoisted — is a non-generic TypeDef, so a use names it with no type
    // arguments and there is no representative closure to choose.
    sealed record UseRepresentation(string SemanticType, string Carrier, Association Local = null);

    static UseRepresentation ResolveUse(JsonObject value, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        if (Str(value["k"]) != "companionValue") return null;
        var semantic = Str((value["companionType"] as JsonObject)?["name"]);
        if (semantic != null)
        {
            var association = local.SingleOrDefault(a => a.SemanticName == semantic);
            if (association != null)
                return new(association.SemanticName, association.PhysicalName, association);
        }
        if (!bindExternal || Str((value["ownerType"] as JsonObject)?["name"]) is not string physicalOwner) return null;
        if (refs.TryCompanionMetadataCarrier(physicalOwner, out var carrier) ||
            refs.TryCompanionCarrierByPhysicalOwner(physicalOwner, out carrier))
            return new(semantic, carrier);
        return null;
    }

    static UseRepresentation ResolveMarker(string marker, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        var association = local.SingleOrDefault(a => a.SemanticName == marker);
        if (association != null)
            return new(association.SemanticName, association.PhysicalName, association);
        if (!bindExternal) return null;
        var semantic = marker;
        var physical = marker;
        if (refs.TryCompanionPhysicalOwner(marker, out var selected)) physical = selected;
        else if (!refs.TryCompanionSemanticType(marker, out semantic)) return null;
        return (refs.TryCompanionMetadataCarrier(physical, out var carrier) ||
                refs.TryCompanionCarrierByPhysicalOwner(physical, out carrier))
            ? new(semantic, carrier)
            : null;
    }

    static void RewriteMarkedCaptures(
        JsonNode node, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["companionCaptureOwner"]) is string marker)
            {
                var representation = ResolveMarker(marker, local, refs, bindExternal);
                // Preserve the old RefBuild rule: referenced-assembly companion receivers are not physically bound
                // in a reference build. Drop only this transient marker and leave the capture/body for RefBodySquash.
                if (representation == null && !bindExternal && !marker.Contains("<companion:", StringComparison.Ordinal))
                {
                    obj.Remove("companionCaptureOwner");
                    foreach (var child in obj.ToArray())
                        if (child.Value is not null) RewriteMarkedCaptures(child.Value, local, refs, bindExternal);
                    return;
                }
                if (representation == null)
                    throw new InvalidOperationException($"unknown companion capture association '{marker}'");
                NormalizeCaptureTypesAndOwners(obj, representation);
                obj.Remove("companionCaptureOwner");
            }
            foreach (var child in obj.ToArray())
                if (child.Value is not null) RewriteMarkedCaptures(child.Value, local, refs, bindExternal);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child is not null) RewriteMarkedCaptures(child, local, refs, bindExternal);
    }

    static void NormalizeCaptureTypesAndOwners(JsonNode node, UseRepresentation representation, string property = null)
    {
        if (node is JsonObject obj)
        {
            // Keep companionType as the semantic association key until the value binder consumes companionValue.
            // Every physical capture slot and member declaration owner, however, must name the selected CLR type.
            if (property != "companionType" && Str(obj["t"]) == "fqn" &&
                Str(obj["name"]) == representation.SemanticType)
                obj["name"] = representation.Carrier;
            foreach (var child in obj.ToArray())
                if (child.Value is not null)
                    NormalizeCaptureTypesAndOwners(child.Value, representation, child.Key);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child is not null) NormalizeCaptureTypesAndOwners(child, representation, property);
    }

    // Bind values imported through dll2klib from the validated physical companion association. This runs after every
    // raw-BIR splice and before CallEvalLowering, so a receiver/value captured by an evaluation plan is already the
    // concrete carrier INSTANCE.
    public static void BindExternalUses(JsonNode root, ReferenceMetadataIndex refs)
    {
        void Visit(JsonNode node, JsonObject parent = null, string property = null)
        {
            if (node is JsonObject obj)
            {
                // With CompanionBlocksAndExtensions enabled, the frontend represents `Owner.Companion` as the
                // ordinary static source-name accessor emitted on Owner. A generic owner holds one such accessor
                // FIELD per closed instantiation, all pointing at the single hoisted carrier. Consume the trusted
                // association and load that carrier's singleton directly, just as the companionValue form below
                // does, so no use depends on which instantiation the source happened to name.
                if (Str(obj["k"]) == "staticField" &&
                    Str((obj["ownerType"] as JsonObject)?["name"]) is string accessorOwner &&
                    Str(obj["name"]) is string accessorName &&
                    refs.TryCompanionAccessor(accessorOwner, accessorName, out var accessorCarrier))
                {
                    obj.Clear();
                    obj["k"] = "staticField";
                    obj["ownerType"] = PhysicalType(new UseRepresentation(null, accessorCarrier));
                    obj["name"] = "$INSTANCE";
                    return;
                }
                // Physical carrier tokens restored by dll2klib use CIR's source-style spelling. Once the trusted
                // association has been validated, replace every such TypeNode with the exact reflected TypeDef token.
                // This includes declaration slots, not just member owners.
                if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string physicalType &&
                    refs.TryCompanionMetadataCarrier(physicalType, out var exactCarrier))
                    obj["name"] = exactCarrier;
                // A default/inline payload can share the outer call's already-evaluated companion receiver through a
                // bindRef. In that shape there is no companionValue directly under the nested call for the ordinary
                // receiver-driven rewrite below to recognize. The semantic owner token is still an exact trusted
                // association key, so bind that declaration identity directly before lowering the shared receiver.
                var memberKind = Str(obj["k"]);
                if (memberKind is "callInstance" or "newBoundDelegate" or
                    "field" or "setField" or "setFieldExpr" or "lateinitGet" &&
                    Str((obj["ownerType"] as JsonObject)?["name"]) is string semanticMemberOwner &&
                    refs.TryCompanionPhysicalOwner(semanticMemberOwner, out var physicalMemberOwner))
                {
                    obj["ownerType"] = Fqn(physicalMemberOwner);
                    if (memberKind is "callInstance" or "newBoundDelegate") obj["companionCall"] = true;
                    if (memberKind == "newBoundDelegate") obj["calleeOwner"] = Fqn(physicalMemberOwner);
                }
                if (obj["recv"] is JsonObject directReceiver &&
                    ResolveUse(directReceiver, [], refs, bindExternal: true) is { } directRepresentation &&
                    Str((obj["ownerType"] as JsonObject)?["name"]) is string directMemberOwner &&
                    SameCompanionMemberOwner(directMemberOwner, directRepresentation))
                {
                    obj["ownerType"] = PhysicalType(directRepresentation);
                    if (Str(obj["k"]) == "newBoundDelegate")
                        obj["calleeOwner"] = PhysicalType(directRepresentation);
                    directReceiver.Clear();
                    directReceiver["k"] = "staticField";
                    directReceiver["ownerType"] = PhysicalType(directRepresentation);
                    directReceiver["name"] = "$INSTANCE";
                }
                if (ResolveUse(obj, [], refs, bindExternal: true) is { } representation)
                {
                    obj.Clear();
                    obj["k"] = "staticField";
                    obj["ownerType"] = PhysicalType(representation);
                    obj["name"] = "$INSTANCE";
                    return;
                }
                foreach (var child in obj.ToArray())
                    if (child.Value is not null) Visit(child.Value, obj, child.Key);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray())
                    if (child is not null) Visit(child, parent, property);
        }
        Visit(root);
    }

    static bool SameCompanionMemberOwner(string memberOwner, UseRepresentation representation) =>
        memberOwner == representation.SemanticType ||
        memberOwner == representation.Carrier ||
        memberOwner.Replace('+', '.') == representation.Carrier.Replace('+', '.');

    public static void AssertNoCompanionValues(JsonNode root)
    {
        void Visit(JsonNode node, JsonObject parent = null, string property = null)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) == "companionValue")
                    throw new InvalidOperationException(
                        $"companionValue survived CLR companion use binding: owner={Str((obj["ownerType"] as JsonObject)?["name"])} " +
                        $"semantic={Str((obj["companionType"] as JsonObject)?["name"])} " +
                        $"context={(parent is null ? "root" : Str(parent["k"]) ?? Str(parent["kind"]) ?? "object")}.{property} " +
                        $"member={Str(parent?["method"])} memberOwner={Str((parent?["ownerType"] as JsonObject)?["name"])}");
                foreach (var child in obj)
                    if (child.Value is not null) Visit(child.Value, obj, child.Key);
            }
            else if (node is JsonArray array)
                foreach (var child in array)
                    if (child is not null) Visit(child, parent, property);
        }
        Visit(root);
    }

    static void Materialize(Association a)
    {
        a.Companion["name"] = a.PhysicalName;
        // A generic owner cannot host a non-generic nested TypeDef, so the carrier of a generic owner is top-level and
        // holds the one singleton every closed instantiation shares. A non-generic owner keeps CLR nesting, where that
        // singleton is already unique and the lexical relation is free.
        if (a.Hoisted) a.Companion.Remove("nestedIn");
        else a.Companion["nestedIn"] = a.OwnerName;
        a.Companion.Remove("kotlinCompanion");
        // This compiler-reserved TypeDef is an implementation carrier, not the Kotlin companion declaration. Keep it
        // public so lifted callable-reference/state-machine helpers can name it after leaving a protected lexical
        // scope. A nested carrier's enclosing owner still caps effective CLR visibility, the source-name field below
        // carries the companion's Kotlin visibility, and explicit companion metadata restores that visibility on
        // re-import.
        a.Companion["vis"] = "public";
        a.Companion["generated"] = true;
        var mods = a.Companion["mods"] as JsonObject ?? new JsonObject();
        mods["object"] = true;
        a.Companion["mods"] = mods;
        var fields = a.Companion["fields"] as JsonArray ?? new JsonArray();
        fields.Insert(0, new JsonObject {
            ["name"] = "$INSTANCE",
            ["type"] = PhysicalType(a),
            ["static"] = true,
            ["init"] = new JsonObject {
                ["k"] = "new", ["type"] = PhysicalType(a),
                ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
            },
        });
        a.Companion["fields"] = fields;
        a.Companion["companionCarrier"] = Carrier(a);

        // The source companion name is the ordinary CLR value surface on the owner. A generic owner holds one such
        // FIELD per closed instantiation — CLR static storage is per closed type and no representation can change
        // that — but every one of them is initialized from the single carrier singleton, so the VALUE a CLR consumer
        // reads off `Owner<A>` and `Owner<B>` is one and the same companion. The implementation type keeps its
        // compiler-reserved `$` spelling so it cannot collide with a legal Kotlin declaration or a companion member
        // such as `INSTANCE`.
        // A basic CLR enum may own the nested carrier but cannot own the .cctor required to initialize a reference-valued
        // static accessor. Preserve its enum representation and Kotlin round-trip; its C# source-name accessor is the
        // one deliberate omission, rather than silently degrading the source enum to a class.
        if (Str(a.Owner["kind"]) == "enum") return;
        var ownerFields = a.Owner["fields"] as JsonArray ?? new JsonArray();
        if (ownerFields.OfType<JsonObject>().Any(f => Str(f["name"]) == a.SourceName))
            throw new InvalidOperationException(
                $"companion value accessor '{a.OwnerName}.{a.SourceName}' collides with an existing field");
        ownerFields.Add(new JsonObject {
            ["name"] = a.SourceName,
            ["type"] = PhysicalType(a),
            ["static"] = true,
            ["vis"] = a.Visibility,
            ["init"] = new JsonObject {
                ["k"] = "staticField",
                ["ownerType"] = PhysicalType(a),
                ["name"] = "$INSTANCE",
            },
        });
        a.Owner["fields"] = ownerFields;
    }

    static int PhysicalTypeParamCount(JsonObject type) =>
        ((type["capturedTypeParams"] as JsonArray)?.Count ?? 0) +
        ((type["typeParams"] as JsonArray)?.Count ?? 0);

    static JsonObject PhysicalType(Association a) => Fqn(a.PhysicalName);

    static JsonObject PhysicalType(UseRepresentation representation) => Fqn(representation.Carrier);

    static JsonObject Carrier(Association a) => new() {
        ["kind"] = a.Hoisted ? "sidecar" : "nested",
        ["owner"] = a.KotlinOwnerName,
        ["name"] = a.SourceName,
        ["visibility"] = a.Visibility,
        ["physicalOwner"] = a.PhysicalOwner,
        ["physicalOwnerArity"] = a.PhysicalOwnerArity,
    };

    static string PhysicalMetadataName(JsonObject type, IReadOnlyDictionary<string, JsonObject> byName)
    {
        var name = Str(type["name"]) ?? throw new InvalidOperationException("companion owner has no physical name");
        // Companion representation runs before the general ownership pass, so an ordinary Kotlin child still carries
        // BIR `semanticOwner` here; an already-materialized physical helper may carry CIR `nestedIn`. Follow either
        // fact to author the exact `+`-separated metadata owner recorded in [KotlinCompanion]. A file facade is not a
        // type declaration in `byName`, but is nevertheless the final physical parent and therefore a valid root.
        var parentName = Str(type["nestedIn"]) ?? Str(type["semanticOwner"]);
        if (parentName is null) return name;
        var physicalParent = byName.TryGetValue(parentName, out var parent)
            ? PhysicalMetadataName(parent, byName)
            : parentName;
        return physicalParent + "+" + name[(name.LastIndexOf('.') + 1)..];
    }

    static void RewriteUses(JsonNode node, IReadOnlyList<Association> associations)
    {
        if (node is JsonObject obj)
        {
            foreach (var association in associations)
            {
                var physicalType = PhysicalType(association);
                if (obj["t"] is JsonValue && Str(obj["name"]) == association.SemanticName)
                    Replace(obj, physicalType);

                var kind = Str(obj["k"]);
                var memberOwner = Str((obj["ownerType"] as JsonObject)?["name"]);
                if (kind is "callInstance" or "field" or "setField" or "setFieldExpr" or "lateinitGet" &&
                    IsCompanionMemberOwner(memberOwner, association) &&
                    IsCompanionReceiver(obj["recv"], association))
                {
                    obj["ownerType"] = physicalType.DeepClone();
                }
                else if (kind == "newBoundDelegate" &&
                    IsCompanionMemberOwner(memberOwner, association) &&
                    IsCompanionReceiver(obj["recv"], association))
                {
                    obj["ownerType"] = physicalType.DeepClone();
                    obj["calleeOwner"] = physicalType.DeepClone();
                }
                else if (kind == "companionValue" &&
                    Str((obj["companionType"] as JsonObject)?["name"]) == association.SemanticName)
                {
                    obj.Clear();
                    obj["k"] = "staticField";
                    obj["ownerType"] = physicalType;
                    obj["name"] = "$INSTANCE";
                }
            }
            foreach (var child in obj.ToArray())
                if (child.Value is not null) RewriteUses(child.Value, associations);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child is not null) RewriteUses(child, associations);
    }
    static void Replace(JsonObject target, JsonObject replacement)
    {
        target.Clear();
        foreach (var property in replacement) target[property.Key] = property.Value?.DeepClone();
    }

    static bool IsCompanionReceiver(JsonNode node, Association a) =>
        node is JsonObject o && Str(o["k"]) == "companionValue" &&
        Str((o["companionType"] as JsonObject)?["name"]) == a.SemanticName;
    static bool IsCompanionMemberOwner(string owner, Association a) =>
        owner == a.SemanticName || owner == a.PhysicalName;
    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };
    static string Required(JsonObject o, string key) => Str(o[key])
        ?? throw new InvalidOperationException($"malformed companion fact: '{key}' is required");
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();
}
