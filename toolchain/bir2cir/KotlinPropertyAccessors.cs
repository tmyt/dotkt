using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin property accessor identity is an explicit BIR fact.  This is the sole forward projection from that
// identity to the dedicated CLR accessor spelling. No caller may parse
// the resulting method name to recover the property or accessor role.
static class KotlinPropertyAccessors
{
    internal const string SourceNameKey = "propertyName";
    internal const string KindKey = "propertyAccessor";
    internal const string PropertyRolesKey = "kotlinAccessors";
    internal const string AssociationKey = "propertyAssociation";
    // Frontend-selected Kotlin override facts. bir2cir consumes these while allocating CLR slots and never attempts
    // to recover the selected implementation from hierarchy bodies or physical names.
    internal const string InheritedImplementationKey = "inheritedImplementation";
    internal const string InheritedDefaultAccessorsKey = "inheritedDefaultAccessors";
    internal const string InheritedDefaultMethodsKey = "inheritedDefaultMethods";
    // Suspend lowering projects one Kotlin declaration into a public Task method and a continuation cold entry.
    // These bir2cir-only carriers preserve the frontend declaration signature on each physical projection so later
    // slot allocation can consume the selected declaration identity without reconstructing it from either ABI shape.
    internal const string SuspendSourceParamsKey = "suspendSourceParams";
    internal const string SuspendSourceRetKey = "suspendSourceRet";
    // A bare result erasure on a logical suspend override becomes a nested Task<R> erasure after suspend lowering.
    // The early slot pass states that required physical Task result explicitly so the generated TCS/body and the
    // eventual MethodDef signature are authored together; the late pass must never rewrite only the signature.
    internal const string SuspendTaskResultKey = "suspendTaskResult";
    // A declaration synthesized solely to carry a CLR MethodImpl is not a Kotlin declaration candidate. MethodImpl
    // descriptors may also live directly on a source accessor when only its physical name differs from an external
    // property slot, so descriptor presence alone cannot distinguish the two roles.
    internal const string PhysicalSlotBridgeKey = "physicalSlotBridge";
    // CIR instruction: a private forwarding MethodDef declared on an interface is the final body of an explicit
    // MethodImpl. The CLR requires that interface MethodImpl body to be final; ilemit maps this fact one-to-one.
    internal const string ClrInterfaceSlotBridgeKey = "clrInterfaceSlotBridge";
    // A CLR Property signature cannot own method generic parameters. Generic top-level extension properties therefore
    // carry their exact accessor association on the MethodDef instead of emitting an invalid Property row.
    internal const string MetadataCarrierKey = "kotlinPropertyAccessorCarrier";
    internal static string PhysicalName(string sourceName, string kind) => kind switch
    {
        "get" => "prop_get<" + sourceName + ">",
        "set" => "prop_set<" + sourceName + ">",
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
        declaration[PhysicalSlotBridgeKey] is JsonValue value
        && value.TryGetValue<bool>(out var physicalSlotBridge)
        && physicalSlotBridge;

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
        Walk(root, allocateCalls: true, refs, localAccessors, stripPropertyIdentity: false);

    // #395 may rename an accessor only after CLR type lowering exposes its real MethodDef collision set. Keep #397's
    // exact association alive until that allocation is complete, then rebuild the Property descriptor from the
    // associated MethodDef names and consume the BIR-only facts. No get_/set_ spelling is parsed in either direction.
    public static void FinalizePhysicalProperties(IEnumerable<JsonNode> roots)
    {
        var rootObjects = roots.OfType<JsonObject>().ToArray();

        // Equal file facades remain separate CIR roots until ilemit merges their MethodDef and PropertyDef tables.
        // Allocate their top-level Property rows as one physical owner too; otherwise distinct explicit accessor
        // names can repair a collision within one source file but not the identical collision across two files.
        foreach (var owners in rootObjects.GroupBy(root => Str(root["fileClass"]) ?? "<file>"))
            FinalizeOwners(owners.ToArray());

        foreach (var root in rootObjects) Finalize(root, allocateCurrentOwner: false);

        static void FinalizeOwners(IReadOnlyList<JsonObject> owners)
        {
            var propertyOwners = new List<(JsonObject Property, IReadOnlyList<JsonObject> Methods)>();
            foreach (var owner in owners)
            {
                var methods = owner["methods"] is JsonArray methodArray
                    ? methodArray.OfType<JsonObject>().ToArray()
                    : Array.Empty<JsonObject>();
                if (owner["properties"] is not JsonArray propertyArray) continue;
                foreach (var property in propertyArray.OfType<JsonObject>())
                {
                    // Associations are file-local frontend facts. Resolve each row only against its own root before
                    // combining the resulting physical PropertyDef identities under the shared file-facade owner.
                    if (property[PropertyRolesKey] is JsonArray)
                        AllocateProperty(property, methods, stripIdentity: false);
                    propertyOwners.Add((property, methods));
                }
            }
            // Property rows without a Kotlin accessor association are already physical CIR declarations. They are
            // not candidates for source-name allocation; the final module-wide validator still rejects duplicates.
            AllocatePhysicalPropertyNames(propertyOwners
                .Where(candidate => Str(candidate.Property[AssociationKey]) != null)
                .ToArray());
            foreach (var (property, _) in propertyOwners)
            {
                property.Remove(PropertyRolesKey);
                property.Remove(AssociationKey);
            }
        }

        static void Finalize(JsonNode node, bool allocateCurrentOwner = true)
        {
            if (node is JsonObject obj)
            {
                if (allocateCurrentOwner) FinalizeOwners(new[] { obj });
                foreach (var child in obj.Select(pair => pair.Value).ToArray())
                    if (child != null) Finalize(child);
                if (TryIdentity(obj, out _, out _)) RemoveIdentity(obj);
                obj.Remove(PhysicalSlotBridgeKey);
                obj.Remove(DeclarationIdentityBinding.ExplicitNameKey);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToArray()) if (child != null) Finalize(child);
        }
    }

    // A CLR Property row has its own metadata identity: owner + name + property type + index-parameter types.
    // Renaming only the associated MethodDefs is therefore insufficient when two Kotlin extension properties erase
    // to the same CLI signature. Allocate the Property spelling from the same frontend declaration identity, after
    // type lowering and after the exact accessor association has been finalized. dll2klib restores the Kotlin source
    // name from the accessor's [KotlinDeclarationIdentity] carrier; it never reverse-engineers the physical name.
    static void AllocatePhysicalPropertyNames(
        IReadOnlyList<(JsonObject Property, IReadOnlyList<JsonObject> Methods)> properties)
    {
        string PropertySignature(JsonObject property)
        {
            var type = TypeJson.Read(property["type"])
                ?? throw new InvalidOperationException("Kotlin property record has no physical type");
            IEnumerable<JsonNode> indexParameters;
            if (property["getSig"] is JsonArray getSig)
                indexParameters = getSig;
            else if (property["setSig"] is JsonArray setSig)
                indexParameters = setSig.Take(Math.Max(0, setSig.Count - 1));
            else
                throw new InvalidOperationException("Kotlin property record has no physical accessor signature");
            return DeclarationIdentityBinding.PhysicalPropertyTypeSignature(type) + "|" + string.Join(";",
                indexParameters.Select(node => DeclarationIdentityBinding.PhysicalPropertyTypeSignature(
                    TypeJson.Read(node)
                    ?? throw new InvalidOperationException("Kotlin property record has an untyped physical index parameter"))));
        }

        bool TryPropertyDeclarationId(JsonObject property, IReadOnlyList<JsonObject> methods,
            out string declarationId)
        {
            var association = Str(property[AssociationKey])
                ?? throw new InvalidOperationException("Kotlin property record has no accessor association");
            var ids = methods.Where(method => Str(method[AssociationKey]) == association)
                .Select(method => Str(method[DeclarationIdentityBinding.Key]))
                .Where(id => id != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0)
            {
                declarationId = null!;
                return false;
            }
            // A var has distinct getter/setter declaration identities. The getter is the stable representative;
            // a setter-only metadata shape (not authored by Kotlin source) falls back to its sole accessor.
            var getter = methods.SingleOrDefault(method =>
                Str(method[AssociationKey]) == association && Str(method[KindKey]) == "get");
            declarationId = Str(getter?[DeclarationIdentityBinding.Key]) ?? ids[0]!;
            return true;
        }

        string ExplicitPropertyName(JsonObject property, IReadOnlyList<JsonObject> methods)
        {
            var association = Str(property[AssociationKey])
                ?? throw new InvalidOperationException("Kotlin property record has no accessor association");
            // A CLR Property row has one name even when its getter and setter MethodDefs have intentionally different
            // explicit names. The getter is the canonical property-name carrier; a setter-only projected shape uses
            // its setter. This is role-based and never depends on declaration order.
            var getter = methods.SingleOrDefault(method =>
                Str(method[AssociationKey]) == association && Str(method[KindKey]) == "get");
            if (getter != null && Str(getter[DeclarationIdentityBinding.ExplicitNameKey]) != null)
                return Str(getter["name"]);
            if (getter != null) return null;
            var setter = methods.SingleOrDefault(method =>
                Str(method[AssociationKey]) == association && Str(method[KindKey]) == "set");
            return setter != null && Str(setter[DeclarationIdentityBinding.ExplicitNameKey]) != null
                ? Str(setter["name"])
                : null;
        }

        foreach (var group in properties.GroupBy(candidate =>
            (Name: Str(candidate.Property["name"]) ?? throw new InvalidOperationException("property has no name"),
             Signature: PropertySignature(candidate.Property))))
        {
            var colliding = group.ToArray();
            if (colliding.Length < 2) continue;
            var allocation = colliding.Select(candidate =>
            {
                var property = candidate.Property;
                var methods = candidate.Methods;
                var association = Str(property[AssociationKey])!;
                return (Property: property, Association: association,
                    Methods: methods,
                    HasDeclarationId: TryPropertyDeclarationId(property, methods, out var id), DeclarationId: id,
                    ExplicitName: ExplicitPropertyName(property, methods),
                    IsBridge: association.StartsWith("dotkt$bridge$property$", StringComparison.Ordinal));
            }).ToArray();
            var unidentifiedDeclarations = allocation.Where(candidate =>
                !candidate.HasDeclarationId && !candidate.IsBridge).ToArray();
            if (unidentifiedDeclarations.Length > 1)
                throw new InvalidOperationException(
                    $"Kotlin properties named '{group.Key.Name}' with physical signature '{group.Key.Signature}' " +
                    $"collide without frontend declaration identity: " +
                    string.Join(", ", unidentifiedDeclarations.Select(candidate => candidate.Association)));
            foreach (var candidate in allocation)
            {
                var property = candidate.Property;
                var association = candidate.Association;
                var sourceName = group.Key.Name;
                if (candidate.HasDeclarationId)
                {
                    // Once the Property row receives a different physical name, MethodSemantics alone can only report that
                    // physical spelling. Preserve #397's already-resolved semantic association explicitly on each
                    // accessor so dll2klib restores the source property without parsing either physical name.
                    foreach (var method in candidate.Methods.Where(method =>
                                 Str(method[AssociationKey]) == association))
                    {
                        var kind = Str(method[KindKey])
                            ?? throw new InvalidOperationException(
                                $"Kotlin property association '{association}' has an accessor without a role");
                        method[MetadataCarrierKey] = new JsonObject
                        {
                            ["name"] = sourceName,
                            ["kind"] = kind,
                            ["association"] = association,
                        };
                    }
                }
                // A MethodImpl bridge is a physical declaration synthesized by bir2cir, not a declaration FIR can
                // select. Its exact bridge association is therefore the authoritative physical identity. If the
                // collision set contains one ordinary declaration without a frontend identity, keep its existing
                // spelling and move only the bridge(s); two such declarations are refused above rather than guessed.
                if (candidate.ExplicitName != null)
                    property["name"] = candidate.ExplicitName;
                else if (candidate.IsBridge)
                    property["name"] = group.Key.Name + "$bridge$" +
                        DeclarationIdentityBinding.StableSuffix(association);
            }
            var remaining = allocation.GroupBy(candidate =>
                    Str(candidate.Property["name"])
                    ?? throw new InvalidOperationException("property has no physical name"))
                .Where(names => names.Count() > 1)
                .ToArray();
            if (remaining.Length != 0)
                throw new InvalidOperationException(
                    $"bir2cir: CLR physical property signature collision for '{group.Key.Name}' " +
                    $"with signature '{group.Key.Signature}': " +
                    string.Join(", ", remaining.Select(names => $"'{names.Key}'")) +
                    ". Assign distinct @get:ClrName values (and @set:ClrName values for colliding setters); " +
                    "automatic hash suffixes are not used");
        }
        var crossGroupCollisions = properties.GroupBy(candidate =>
                (Name: Str(candidate.Property["name"])
                    ?? throw new InvalidOperationException("property has no final physical name"),
                 Signature: PropertySignature(candidate.Property)))
            .Where(group => group.Count() > 1)
            .ToArray();
        if (crossGroupCollisions.Length != 0)
            throw new InvalidOperationException(
                "bir2cir: final CLR PropertyDef identities collide after explicit accessor naming: " +
                string.Join(", ", crossGroupCollisions.Select(group =>
                    $"'{group.Key.Name}' with signature '{group.Key.Signature}'")) +
                ". Assign distinct @get:ClrName values; automatic hash suffixes are not used");
    }

    static void Walk(JsonNode node, bool allocateCalls, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
            IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> localAccessors,
        bool stripPropertyIdentity = false)
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
                        // Virtual is a declaration fact, but Kotlin `super` has already selected non-virtual
                        // dispatch. Allocating the referenced accessor name must not turn that call back into a
                        // recursive callvirt on the current override.
                        if (referencedVirtual && (obj["super"] as JsonValue)?.GetValue<bool>() != true)
                            obj["virtual"] = true;
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
                        // identity through SetPhysicalName. Ordinary kotc declarations still carry their semantic
                        // source name here and receive the uniform dedicated accessor spelling now.
                        if (current == sourceName) SetPhysicalName(method, PhysicalName(sourceName, kind));
                    }

            if (obj["properties"] is JsonArray properties)
            {
                var ownerMethods = obj["methods"] is JsonArray ownerMethodArray
                    ? ownerMethodArray.OfType<JsonObject>().ToArray()
                    : Array.Empty<JsonObject>();
                var unrepresentable = new List<JsonObject>();
                foreach (var property in properties.OfType<JsonObject>())
                    if (!AllocateProperty(property, ownerMethods, stripPropertyIdentity))
                        unrepresentable.Add(property);
                if (allocateCalls)
                    foreach (var property in unrepresentable)
                        properties.Remove(property);
            }

            foreach (var child in obj.Select(pair => pair.Value).ToArray())
                if (child != null) Walk(child, allocateCalls, refs, localAccessors, stripPropertyIdentity);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToArray())
                if (child != null) Walk(child, allocateCalls, refs, localAccessors, stripPropertyIdentity);
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
        // Preserve the opaque declaration association when MethodSemantics cannot carry it, or when a synthesized
        // bridge explicitly points back to this source association. A referenced signature-changing MethodImpl body
        // cannot be paired back to its source by comparing their deliberately different physical signatures.
        var isBridgeSource = methods.Any(candidate =>
            Str((candidate[MetadataCarrierKey] as JsonObject)?["sourceAssociation"]) == association);
        foreach (var (method, role) in resolved)
        {
            var sourceAssociation = Str((method[MetadataCarrierKey] as JsonObject)?["sourceAssociation"]);
            if (representable && sourceAssociation == null && !isBridgeSource) continue;
            var carrier = new JsonObject
            {
                ["name"] = sourceName,
                ["kind"] = role,
                ["association"] = association,
            };
            // AssociateBridgeProperty installs this explicit source relation before the final allocation sweep.
            // Rebuilding the bridge's physical Property descriptor must not collapse its four-field carrier back to
            // an ordinary source accessor's three fields.
            if (sourceAssociation != null) carrier["sourceAssociation"] = sourceAssociation;
            method[MetadataCarrierKey] = carrier;
        }

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
            ["sourceAssociation"] = sourceAssociation,
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
        {
            var associatedMethods = owner["methods"] is JsonArray ownerMethods
                ? string.Join(",", ownerMethods.OfType<JsonObject>()
                    .Where(method => Str(method[AssociationKey]) == association)
                    .Select(method => Str(method["name"])))
                : "";
            throw new InvalidOperationException(
                $"property override bridges do not form one unique CLR Property shape: " +
                $"owner={Str(owner["name"]) ?? "<file>"}, property={propertyName}, accessor={accessorKind}, " +
                $"bridge={Str(bridge["name"])}, association={association}, methods={associatedMethods}");
        }
        propertyRoles.Add(accessorKind);
    }

    // An exact-signature interface bridge exists only because the source accessor and external CLR accessor have
    // different physical names. Preserve its Kotlin role for round-trip de-duplication without emitting a second,
    // duplicate CLR Property row for the same source property shape.
    internal static void MarkExactInterfaceBridgeProperty(JsonObject bridge, string propertyName,
        string accessorKind, string sourceAssociation)
    {
        if (accessorKind is not ("get" or "set"))
            throw new InvalidOperationException("property override bridge has no getter/setter role");
        if (string.IsNullOrEmpty(sourceAssociation))
            throw new InvalidOperationException("property override bridge has no source association");
        var association = "dotkt$bridge$property$" + sourceAssociation + "|exact-interface";
        bridge[SourceNameKey] = propertyName;
        bridge[KindKey] = accessorKind;
        bridge[AssociationKey] = association;
        bridge[MetadataCarrierKey] = new JsonObject
        {
            ["name"] = propertyName,
            ["kind"] = accessorKind,
            ["association"] = association,
            ["sourceAssociation"] = sourceAssociation,
        };
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
