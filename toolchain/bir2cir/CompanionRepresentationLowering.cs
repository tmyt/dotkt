using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Consumes kotc's representation-neutral companion declarations before any ordinary BIR pass sees them.
// Every companion becomes one compiler-named ordinary CLR class carrying exactly one `$INSTANCE`, so the Kotlin
// declaration and its singleton stay one-to-one. CLR static storage belongs to each closed constructed generic type,
// so a carrier owned by a generic type would multiply that singleton per instantiation; a companion whose physical
// owner has ANY generic slot is therefore HOISTED out of it to a non-generic top-level sidecar and the owner keeps
// only a source-named accessor field pointing at it. A non-generic owner keeps its nested carrier, which is already
// one closed type. Neither shape has generic captures, so no companion carries type parameters at all.
// No semantic companion node reaches CIR.
static partial class CompanionRepresentationLowering
{
    // Separates a hoisted carrier's flattened owner path from the companion's source name. See Apply below for why a
    // bare `$` is not enough.
    internal const string HoistedMarker = "$companion$";

    internal sealed record Association(
        string OwnerName, string KotlinOwnerName, string SemanticName,
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
    public static void BindSpliceUses(BirFileDeclaration root, ReferenceMetadataIndex refs)
        => root.Visit(node => {
            if (node is BirCallInline call && call.Owner.OrDefault() is TypeNode.Fqn owner
                && refs.TryCompanionPhysicalOwner(owner.Name, out var physicalOwner))
                call.Owner = owner with { Name = physicalOwner };
        });

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
        if (!bindExternal) return null;
        // The source owner can differ from its CLR nesting after static-classifier placement. Bind the companion's
        // explicit identity, rather than requiring its containing source classifier to already be a physical token.
        if (semantic != null && (refs.TryCompanionPhysicalOwner(semantic, out var selectedCarrier) ||
            refs.TryCompanionMetadataCarrier(semantic, out selectedCarrier)))
            return new(semantic, selectedCarrier);
        if (Str((value["ownerType"] as JsonObject)?["name"]) is not string physicalOwner) return null;
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
                    ReplaceWithSingletonLoad(obj, PhysicalType(new UseRepresentation(null, accessorCarrier)));
                    return;
                }
                // Physical carrier tokens restored by dll2klib use CIR's source-style spelling. Once the trusted
                // association has been validated, replace every such TypeNode with the exact reflected TypeDef token.
                // This includes declaration slots, not just member owners. A materialized default may still carry
                // the semantic companion identity in its evaluation-plan type while its value already binds to the
                // physical singleton; resolve both through the same trusted association.
                if (Str(obj["t"]) == "fqn" && Str(obj["name"]) is string physicalType &&
                    (refs.TryCompanionMetadataCarrier(physicalType, out var exactCarrier) ||
                     refs.TryCompanionPhysicalOwner(physicalType, out exactCarrier)))
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
                    ReplaceWithSingletonLoad(directReceiver, PhysicalType(directRepresentation));
                }
                if (ResolveUse(obj, [], refs, bindExternal: true) is { } representation)
                {
                    ReplaceWithSingletonLoad(obj, PhysicalType(representation));
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

    static JsonObject PhysicalType(Association a) => Fqn(a.PhysicalName);

    static JsonObject PhysicalType(UseRepresentation representation) => Fqn(representation.Carrier);

    static void RewriteUses(JsonNode node, IReadOnlyList<Association> associations)
    {
        if (node is JsonObject obj)
        {
            foreach (var association in associations)
            {
                if (obj["t"] is JsonValue && Str(obj["name"]) == association.SemanticName)
                    Replace(obj, PhysicalType(association));
                if (Str(obj["semanticOwner"]) == association.SemanticName)
                    obj["semanticOwner"] = association.PhysicalName;

                var kind = Str(obj["k"]);
                var memberOwner = Str((obj["ownerType"] as JsonObject)?["name"]);
                if (kind is "callInstance" or "field" or "setField" or "setFieldExpr" or "lateinitGet" &&
                    IsCompanionMemberOwner(memberOwner, association) &&
                    IsCompanionReceiver(obj["recv"], association))
                {
                    obj["ownerType"] = PhysicalType(association);
                }
                else if (kind == "newBoundDelegate" &&
                    IsCompanionMemberOwner(memberOwner, association) &&
                    IsCompanionReceiver(obj["recv"], association))
                {
                    obj["ownerType"] = PhysicalType(association);
                    obj["calleeOwner"] = PhysicalType(association);
                }
                else if (kind == "companionValue" &&
                    Str((obj["companionType"] as JsonObject)?["name"]) == association.SemanticName)
                {
                    ReplaceWithSingletonLoad(obj, PhysicalType(association));
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

    // A companion value is represented by its carrier's self-typed singleton field. The field owner and the value
    // type happen to be equal for this compiler-authored field, but that is a fact of the companion representation,
    // not a general rule for static fields. State it here at the producer so an early pass that materialises the
    // value (notably suspend operand planning) never has to infer a field's value type from its declaring owner.
    static JsonObject SingletonLoad(JsonObject physicalType) => new() {
        ["k"] = "staticField",
        ["ownerType"] = physicalType.DeepClone(),
        ["name"] = "$INSTANCE",
        ["sty"] = physicalType.DeepClone(),
    };

    static void ReplaceWithSingletonLoad(JsonObject target, JsonObject physicalType) =>
        Replace(target, SingletonLoad(physicalType));

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };
    static string Str(JsonNode node) => (node as JsonValue)?.GetValue<string>();
}
