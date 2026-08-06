using System.Text.Json.Nodes;
using DotKt.Bir;

// Consumes kotc's representation-neutral companion declarations before any ordinary BIR pass sees them.
// Every companion becomes one compiler-named ordinary CLR nested class. A generic owner contributes explicit,
// unconstrained physical capture slots; Kotlin-facing uses close those slots with object while CLR consumers may
// close the owner normally. No semantic companion node reaches CIR.
static class CompanionRepresentationLowering
{
    internal sealed record Association(
        JsonObject Owner, JsonObject Companion, string OwnerName, string KotlinOwnerName, string SemanticName,
        string SourceName, string Visibility, string PhysicalName, string PhysicalOwner, int CaptureArity);
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
            // `$` cannot be written in a Kotlin identifier, including a backticked one. The nested TypeDef is thus
            // collision-proof and intentionally unspeakable from Kotlin; the source name is restored only from the
            // explicit carrier metadata, never inferred from this physical spelling.
            var physicalName = declarationOwnerName + ".$" + sourceName;
            if (types.Any(t => !ReferenceEquals(t, companion) && Str(t["name"]) == physicalName))
                throw new InvalidOperationException(
                    $"reserved physical companion type identity '{physicalName}' is already declared");
            var captureArity = PhysicalTypeParamCount(owner);
            associations.Add(new(owner, companion, declarationOwnerName, ownerName, semanticName,
                sourceName, visibility, physicalName, PhysicalMetadataName(owner, byName), captureArity));
            owner.Remove("kotlinCompanion");
        }

        foreach (var association in associations)
            MaterializeNested(association);
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

    sealed record UseRepresentation(
        string SemanticType, string Carrier, int CaptureArity,
        Association Local = null);

    static UseRepresentation ResolveUse(JsonObject value, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        if (Str(value["k"]) != "companionValue") return null;
        var semantic = Str((value["companionType"] as JsonObject)?["name"]);
        if (semantic != null)
        {
            var association = local.SingleOrDefault(a => a.SemanticName == semantic);
            if (association != null)
                return new(association.SemanticName, association.PhysicalName, association.CaptureArity, association);
        }
        if (!bindExternal || Str((value["ownerType"] as JsonObject)?["name"]) is not string physicalOwner) return null;
        if (refs.TryCompanionMetadataCarrier(physicalOwner, out var carrier) ||
            refs.TryCompanionCarrierByPhysicalOwner(physicalOwner, out carrier))
            return new(semantic, carrier, refs.OwnerArity(carrier));
        return null;
    }

    static UseRepresentation ResolveMarker(string marker, IReadOnlyList<Association> local, ReferenceMetadataIndex refs, bool bindExternal)
    {
        var association = local.SingleOrDefault(a => a.SemanticName == marker);
        if (association != null)
            return new(association.SemanticName, association.PhysicalName, association.CaptureArity, association);
        if (!bindExternal) return null;
        var semantic = marker;
        var physical = marker;
        if (refs.TryCompanionPhysicalOwner(marker, out var selected)) physical = selected;
        else if (!refs.TryCompanionSemanticType(marker, out semantic)) return null;
        return (refs.TryCompanionMetadataCarrier(physical, out var carrier) ||
                refs.TryCompanionCarrierByPhysicalOwner(physical, out carrier))
            ? new(semantic, carrier, refs.OwnerArity(carrier))
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
                // ordinary static source-name accessor emitted on Owner. For a generic owner that accessor's CLR
                // field type is tied to the selected outer instantiation, while Kotlin's companion classifier has no
                // such type arguments. Consume the trusted association and load the canonical carrier singleton
                // directly, just as the older companionValue form below does.
                if (Str(obj["k"]) == "staticField" &&
                    Str((obj["ownerType"] as JsonObject)?["name"]) is string accessorOwner &&
                    Str(obj["name"]) is string accessorName &&
                    refs.TryCompanionAccessor(accessorOwner, accessorName, out var accessorCarrier))
                {
                    var accessorRepresentation = new UseRepresentation(
                        null, accessorCarrier, refs.OwnerArity(accessorCarrier));
                    obj.Clear();
                    obj["k"] = "staticField";
                    obj["ownerType"] = PhysicalType(accessorRepresentation);
                    obj["name"] = "$INSTANCE";
                    return;
                }
                // Physical carrier tokens restored by dll2klib use CIR's source-style arity-free spelling. Once the
                // trusted association has been validated, replace every such TypeNode with the exact reflected TypeDef
                // token. This includes declaration/capture slots, not just member owners, and retains any synthetic
                // capture arguments already present on the node.
                if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string physicalType &&
                    refs.TryCompanionMetadataCarrier(physicalType, out var exactCarrier))
                {
                    obj["name"] = exactCarrier;
                    // dll2klib deliberately erases the carrier's synthetic owner-capture parameters from the semantic
                    // companion declaration. Re-close a bare physical token with object here; leaving it open makes
                    // ilemit interpret `!0` in a non-generic consumer method and produces an invalid TypeSpec.
                    if (obj["args"] is null && refs.OwnerArity(exactCarrier) is var arity && arity > 0)
                    {
                        var args = new JsonArray();
                        for (var i = 0; i < arity; i++) args.Add(Fqn("object"));
                        obj["args"] = args;
                    }
                }
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

    static void MaterializeNested(Association a)
    {
        a.Companion["name"] = a.PhysicalName;
        a.Companion["nestedIn"] = a.OwnerName;
        a.Companion.Remove("kotlinCompanion");
        // This compiler-reserved TypeDef is an implementation carrier, not the Kotlin companion declaration. Keep it
        // NestedPublic so lifted callable-reference/state-machine helpers can name it after leaving a protected lexical
        // scope. The enclosing owner still caps effective CLR visibility, the source-name field below carries the
        // companion's Kotlin visibility, and explicit companion metadata restores that visibility on re-import.
        a.Companion["vis"] = "public";
        a.Companion["generated"] = true;
        a.Companion["capturedTypeParams"] = CapturedTypeParams(a.Owner);
        var mods = a.Companion["mods"] as JsonObject ?? new JsonObject();
        mods["object"] = true;
        a.Companion["mods"] = mods;
        var fields = a.Companion["fields"] as JsonArray ?? new JsonArray();
        fields.Insert(0, new JsonObject {
            ["name"] = "$INSTANCE",
            ["type"] = PhysicalType(a, canonical: false),
            ["static"] = true,
            ["init"] = new JsonObject {
                ["k"] = "new", ["type"] = PhysicalType(a, canonical: false),
                ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
            },
        });
        a.Companion["fields"] = fields;
        a.Companion["companionCarrier"] = Carrier(a);

        // The source companion name is the ordinary CLR value surface on each closed outer instantiation. The nested
        // implementation type remains in the compiler-reserved namespace so it cannot collide with a legal Kotlin
        // nested type or a companion member such as `INSTANCE`.
        // A basic CLR enum may own the nested carrier but cannot own the .cctor required to initialize a reference-valued
        // static accessor. Preserve its enum representation and Kotlin round-trip; the C# source-name accessor is the one
        // deliberate exception in this first step rather than silently degrading the source enum to a class.
        if (Str(a.Owner["kind"]) == "enum") return;
        var ownerFields = a.Owner["fields"] as JsonArray ?? new JsonArray();
        if (ownerFields.OfType<JsonObject>().Any(f => Str(f["name"]) == a.SourceName))
            throw new InvalidOperationException(
                $"companion value accessor '{a.OwnerName}.{a.SourceName}' collides with an existing field");
        ownerFields.Add(new JsonObject {
            ["name"] = a.SourceName,
            ["type"] = PhysicalType(a, canonical: false),
            ["static"] = true,
            ["vis"] = a.Visibility,
            ["init"] = new JsonObject {
                ["k"] = "staticField",
                ["ownerType"] = PhysicalType(a, canonical: false),
                ["name"] = "$INSTANCE",
            },
        });
        a.Owner["fields"] = ownerFields;
    }

    static JsonArray CapturedTypeParams(JsonObject owner)
    {
        var result = new JsonArray();
        foreach (var source in new[] { owner["capturedTypeParams"] as JsonArray, owner["typeParams"] as JsonArray })
            foreach (var parameter in source ?? [])
                result.Add(parameter is JsonObject o ? Str(o["name"]) : Str(parameter));
        return result;
    }

    static int PhysicalTypeParamCount(JsonObject type) =>
        ((type["capturedTypeParams"] as JsonArray)?.Count ?? 0) +
        ((type["typeParams"] as JsonArray)?.Count ?? 0);

    static JsonObject PhysicalType(Association a, bool canonical)
    {
        var args = new JsonArray();
        for (var i = 0; i < a.CaptureArity; i++)
            args.Add(canonical
                ? Fqn("object")
                : new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = i });
        return Fqn(a.PhysicalName, args);
    }

    static JsonObject PhysicalType(UseRepresentation representation)
    {
        var args = new JsonArray();
        for (var i = 0; i < representation.CaptureArity; i++) args.Add(Fqn("object"));
        return Fqn(representation.Carrier, args);
    }

    static JsonObject Carrier(Association a) => new() {
        ["kind"] = "nested",
        ["owner"] = a.KotlinOwnerName,
        ["name"] = a.SourceName,
        ["visibility"] = a.Visibility,
        ["physicalOwner"] = a.PhysicalOwner,
        ["physicalOwnerArity"] = (a.Owner["typeParams"] as JsonArray)?.Count ?? 0,
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

    static void RewriteUses(
        JsonNode node, IReadOnlyList<Association> associations, Association carrierScope = null)
    {
        if (node is JsonObject obj)
        {
            carrierScope ??= associations.SingleOrDefault(a => ReferenceEquals(obj, a.Companion));
            foreach (var association in associations)
            {
                var physicalType = carrierScope == association
                    ? PhysicalType(association, canonical: false)
                    : PhysicalType(association, canonical: true);
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
                if (child.Value is not null) RewriteUses(child.Value, associations, carrierScope);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child is not null) RewriteUses(child, associations, carrierScope);
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
    static JsonObject Fqn(string name, JsonArray args = null)
    {
        var result = new JsonObject { ["t"] = "fqn", ["name"] = name };
        if (args is { Count: > 0 }) result["args"] = args;
        return result;
    }
    static string Required(JsonObject o, string key) => Str(o[key])
        ?? throw new InvalidOperationException($"malformed companion fact: '{key}' is required");
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();
}
