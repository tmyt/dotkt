using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// #395 — frontend-selected Kotlin callable identity -> one physical CLR MethodDef.
//
// Kotlin can distinguish declarations whose receiver/parameter types collapse to the same CLR type. The selected
// IrFunction's stable pre-CLR fingerprint rides BIR as `declarationId`; this pass runs only after
// BirTypeLowering has exposed the real CLI collision set. It allocates a stable physical name once and rewrites
// declarations and every local use by that same authoritative key. No signature/name resolver is allowed to
// rediscover the Kotlin declaration after erasure.
static class DeclarationIdentityBinding
{
    internal const string Key = "declarationId";
    internal const string ExplicitNameKey = "explicitClrName";
    internal const string SemanticSignatureKey = "declarationSemanticSignature";
    internal const string ReferencedIntrinsicKey = "declarationReferencedIntrinsic";
    internal const string ReferencedFactoryKey = "declarationReferencedFactory";
    internal const string ReferencedSequenceFilterNotNullKey = "declarationReferencedSequenceFilterNotNull";
    internal const string PhysicalOnlySuffix = "|clr-physical-only";

    internal static string PhysicalOnlyId(string declarationId, string role) =>
        declarationId + PhysicalOnlySuffix + "|" + role;

    // Capture Kotlin's declaration spelling before any representation pass renames accessors, companion-extension
    // cores, suspend entries, or other CLR artifacts. Later metadata must never recover this spelling from a physical
    // get_/set_ or compiler-reserved name.
    public static IReadOnlyDictionary<string, JsonObject> PreserveSourceFacts(IEnumerable<JsonNode> roots)
    {
        var semanticSignatures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        void WalkOwner(JsonObject owner)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                {
                    if (Str(method[Key]) is not string id) continue;
                    if (Str(method["name"]) is string name
                        && method["declarationSourceName"] == null)
                        method["declarationSourceName"] = name;
                    if (method["params"] is not JsonArray parameters || method["ret"] == null)
                        throw new InvalidOperationException(
                            $"declaration identity '{method[Key]}' has no complete semantic signature");
                    var signature = new JsonObject
                    {
                        ["params"] = new JsonArray(parameters.OfType<JsonObject>()
                            .Select(parameter => parameter["type"]?.DeepClone()
                                ?? throw new InvalidOperationException(
                                    $"declaration identity '{method[Key]}' has an untyped parameter"))
                            .ToArray()),
                        ["ret"] = method["ret"]!.DeepClone(),
                    };
                    if (semanticSignatures.TryGetValue(id, out var prior) &&
                        prior.ToJsonString() != signature.ToJsonString())
                        throw new InvalidOperationException(
                            $"declaration identity '{id}' has conflicting semantic signatures");
                    semanticSignatures[id] = signature;
                }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) WalkOwner(root);
        return semanticSignatures;
    }

    public static HashSet<string> CollectDeclarationIds(IEnumerable<JsonNode> roots)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void WalkOwner(JsonObject owner)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Str(method[Key]) is string id) result.Add(id);
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) WalkOwner(root);
        return result;
    }

    // A stdlib metadata build cannot read its own future reference DLL, but its BIR already states every
    // @ClrTypeAlias explicitly. Merge those local facts only for the isolated runtime-physical collision projection,
    // so reference and runtime twins allocate the same MethodDef names from the same CLR signature set.
    public static IReadOnlyDictionary<string, string> CollisionAliases(
        IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> referencedAliases)
    {
        var result = new Dictionary<string, string>(referencedAliases, StringComparer.Ordinal);

        void WalkOwner(JsonObject owner)
        {
            if (Str(owner["name"]) is string ownerName && owner["attrs"] is JsonArray attrs)
            {
                foreach (var attr in attrs.OfType<JsonObject>())
                {
                    if (TypeJson.OwnerName(attr["attr"]) != "kotlin.clr.ClrTypeAlias") continue;
                    var alias = attr["args"] is JsonArray args
                        && args.Count == 1
                        && args[0] is JsonObject constant
                        && Str(constant["k"]) == "const"
                        ? Str(constant["value"])
                        : null;
                    if (string.IsNullOrEmpty(alias))
                        throw new InvalidOperationException(
                            $"bir2cir: malformed @ClrTypeAlias on '{ownerName}'");
                    if (result.TryGetValue(ownerName, out var prior) && prior != alias)
                        throw new InvalidOperationException(
                            $"bir2cir: conflicting @ClrTypeAlias for '{ownerName}': '{prior}' and '{alias}'");
                    result[ownerName] = alias;
                }
            }
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
        }

        foreach (var root in roots.OfType<JsonObject>())
        {
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
        }
        return result;
    }

    public static void BindReferenced(
        JsonNode root,
        ReferenceMetadataIndex refs,
        IReadOnlySet<string> localIds,
        bool deferUnknown = false)
    {
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var child in obj.Select(kv => kv.Value).ToList()) if (child != null) Walk(child);
                if (Str(obj["k"]) is not ("callStatic" or "callInstance" or "constrainedCall"
                    or "newDelegate" or "newBoundDelegate" or "newBoundClrDelegate" or "newClrStaticDelegate")
                    || Str(obj[Key]) is not string id)
                    return;
                var nodeKind = Str(obj["k"]);
                if (localIds.Contains(id)) return;
                // `suspend inline val coroutineContext` is an existing semantic intrinsic whose physical target is
                // the current continuation, authored by SuspendColdLowering. Preserve the exact selected call until
                // that pass consumes it; binding it to the reference stub first executes the intentional
                // NotImplementedError body. The shared classifier avoids a second spelling heuristic here.
                if (SuspendColdLowering.IsCoroutineContextRead(obj)) return;
                if (!refs.TryDeclarationIdentity(
                    id, out var physicalName, out var owner, out var intrinsic, out _))
                {
                    if (deferUnknown) return;
                    throw new InvalidOperationException(
                        $"bir2cir: referenced frontend declaration identity '{id}' has no trusted physical binding");
                }
                // NetInteropBinding has already selected the CLR delegate shape for a referenced companion callable
                // reference. A real companion member still needs its identity-allocated MethodDef name and physical
                // carrier owner; otherwise ClrMemberResolution would search again using the erased source signature.
                // An explicitly intrinsic-bound companion was already mapped to its exact CLR target by that pass.
                if (nodeKind is "newBoundClrDelegate" or "newClrStaticDelegate")
                {
                    if (intrinsic == null)
                    {
                        obj["method"] = physicalName;
                        var existingOwner = TypeJson.Read(obj["clrType"]) as TypeNode.Fqn;
                        obj["clrType"] = TypeJson.Write(new TypeNode.Fqn(owner, existingOwner?.Args));
                    }
                    obj.Remove(Key);
                    return;
                }
                // A top-level @ClrIntrinsic declaration has no MethodDef at its semantic file-facade owner: the
                // representation pass below must project the already-selected declaration onto its BCL target. Mark
                // this exact binding for MemberCallSubstitution instead of replacing it with the absent ref-only stub
                // or asking that pass to rediscover an overload from the erased signature.
                if (intrinsic != null)
                {
                    // A direct non-generic top-level callable reference has no forwarding call for
                    // MemberCallSubstitution to rewrite. Materialize its selected CLR static target here; kotc only
                    // supplied the semantic declaration identity and bir2cir remains the sole owner of this physical
                    // delegate shape. Extension/member references use forwarding calls or have already been reshaped
                    // by NetInteropBinding, so a surviving newDelegate must name a fully-qualified static intrinsic.
                    if (nodeKind == "newDelegate")
                    {
                        BindStaticIntrinsicDelegate(obj, id, intrinsic);
                        return;
                    }
                    obj[ReferencedIntrinsicKey] = true;
                    return;
                }
                // Collection/array factories also have a semantic CLR representation rather than an ordinary call in
                // the decomposable case. Preserve the owner-less call until MemberCallSubstitution consumes the
                // factory marker belonging to THIS selected declaration. Looking the marker up by method name after
                // erasure would repeat the overload-selection bug this pass exists to remove.
                // Only a call can be replaced by the factory construction representation. A direct delegate targets
                // the real factory MethodDef, and a call whose exact substitution already declined (the marker is
                // present at the late bind) must also fall through to that same exact physical MethodDef. Re-marking
                // either shape would strand the authoritative identity at the final fail-closed gate.
                if (refs.TryDeclarationFactory(id, out _, out _, out _)
                    && nodeKind == "callStatic"
                    && (obj[ReferencedFactoryKey] == null || deferUnknown))
                {
                    obj[ReferencedFactoryKey] = true;
                    return;
                }
                if (refs.IsDeclarationSequenceFilterNotNull(id)
                    && nodeKind == "callStatic"
                    && (obj[ReferencedSequenceFilterNotNullKey] == null || deferUnknown))
                {
                    obj[ReferencedSequenceFilterNotNullKey] = true;
                    return;
                }
                obj["method"] = physicalName;
                if (Str(obj["k"]) == "callStatic")
                {
                    obj["owner"] = new JsonObject { ["t"] = "fqn", ["name"] = owner };
                    obj["calleeOwner"] = new JsonObject { ["t"] = "fqn", ["name"] = owner };
                }
                else if (Str(obj["k"]) == "newDelegate")
                    obj["calleeOwner"] = new JsonObject { ["t"] = "fqn", ["name"] = owner };
                // The trusted declaration binding already selected the exact getter/setter MethodDef. Leaving the
                // semantic role would make a later property resolver attempt to associate that physical name again.
                obj.Remove("prop");
                obj.Remove(ReferencedFactoryKey);
                obj.Remove(ReferencedSequenceFilterNotNullKey);
                // The early, pre-representation bind must keep the authoritative identity. Suspend lowering derives
                // the producer's independently allocated cold-entry identity (`id|cold`) from it; the ordinary late
                // bind then replaces the provisional hot/cold spelling and consumes the fact. Removing it here made
                // cross-module suspend calls append `$dotkt_suspend` to an already-suffixed hot MethodDef name.
                if (!deferUnknown) obj.Remove(Key);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList()) if (child != null) Walk(child);
        }
        Walk(root);
    }

    static void BindStaticIntrinsicDelegate(JsonObject node, string declarationId, string intrinsic)
    {
        var dot = intrinsic.LastIndexOf('.');
        if (dot <= 0 || dot == intrinsic.Length - 1)
            throw new InvalidOperationException(
                $"bir2cir: selected intrinsic declaration '{declarationId}' cannot be represented as a static callable reference: '{intrinsic}'");
        JsonArray argumentTypes;
        if (node["sig"] is JsonArray signature)
            argumentTypes = (JsonArray)signature.DeepClone();
        else if (TypeJson.Read(node["funcType"]) is TypeNode.Fn functionType)
            argumentTypes = new JsonArray(functionType.DelegateParams.Select(TypeJson.Write).ToArray());
        else
            throw new InvalidOperationException(
                $"bir2cir: selected intrinsic callable reference '{declarationId}' has no semantic parameter signature");

        node["k"] = "newClrStaticDelegate";
        node["clrType"] = TypeJson.Write(new TypeNode.Fqn(intrinsic[..dot]));
        node["method"] = intrinsic[(dot + 1)..];
        node["argTypes"] = argumentTypes;
        foreach (var key in new[]
                 {
                     "owner", "ownerType", "calleeOwner", "sig", "prop", Key,
                     ReferencedIntrinsicKey, ReferencedFactoryKey, ReferencedSequenceFilterNotNullKey,
                 })
            node.Remove(key);
    }

    public static IReadOnlyDictionary<string, string> AllocatePhysicalNames(
        IEnumerable<JsonNode> physicalProjectionRoots,
        out IReadOnlySet<string> semanticCarrierIds)
    {
        static List<(JsonObject Method, string Owner, string Package, string Id, string Name, string SourceName,
            string Sig, string ExplicitName, bool Generated)> Collect(
            IEnumerable<JsonNode> sourceRoots)
        {
            var result = new List<(JsonObject Method, string Owner, string Package, string Id, string Name,
                string SourceName, string Sig, string ExplicitName, bool Generated)>();

            void CollectMethods(JsonObject owner, string ownerName, string packageName, bool generatedOwner)
            {
                if (owner["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                        if (Str(method["name"]) is string name)
                            result.Add((method, ownerName, packageName, Str(method[Key]), name,
                                Str(method["declarationSourceName"]) ?? name, PhysicalSignature(method),
                                Str(method[ExplicitNameKey]), generatedOwner || Bool(method["generated"])));
                if (owner["types"] is JsonArray types)
                    foreach (var type in types.OfType<JsonObject>())
                        CollectMethods(type, Str(type["name"]) ?? ownerName + "/<anonymous>", null,
                            generatedOwner || Bool(type["generated"]));
            }

            foreach (var root in sourceRoots.OfType<JsonObject>())
            {
                var fileClass = Str(root["fileClass"]) ?? "<file>";
                var separator = fileClass.LastIndexOf('.');
                CollectMethods(root, fileClass, separator < 0 ? "" : fileClass[..separator],
                    Bool(root["generated"]));
            }
            return result;
        }

        var allDeclarations = Collect(physicalProjectionRoots);
        var declarations = allDeclarations.Where(declaration => declaration.Id != null).ToArray();
        var carrierIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in declarations.GroupBy(d => (d.Owner, d.Name, d.Sig)))
        {
            var ids = group.Select(d => d.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > 1)
                foreach (var id in ids) carrierIds.Add(id);
        }

        foreach (var declaration in allDeclarations.Where(declaration => declaration.ExplicitName != null))
        {
            ValidateExplicitName(declaration.ExplicitName, declaration.Owner, declaration.SourceName);
            if (declaration.Id == null)
                throw new InvalidOperationException(
                    $"bir2cir: @ClrName on '{declaration.Owner}.{declaration.SourceName}' cannot be applied " +
                    "independently because the frontend supplied no allocatable declaration identity (for example, " +
                    "an open/override family or a local implementation artifact); complete slot-wide explicit-name " +
                    "propagation is not supported");
        }

        var explicitByAllocationId = declarations
            .Where(declaration => declaration.ExplicitName != null)
            .GroupBy(declaration => AllocationIdentity(declaration.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var names = group.Select(declaration => declaration.ExplicitName)
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length != 1)
                    throw new InvalidOperationException(
                        $"bir2cir: declaration identity '{group.Key}' carries conflicting explicit CLR names: " +
                        string.Join(", ", names.Select(name => $"'{name}'")));
                return names[0];
            }, StringComparer.Ordinal);

        string AllocatedName(
            (JsonObject Method, string Owner, string Package, string Id, string Name, string SourceName,
                string Sig, string ExplicitName, bool Generated) declaration)
        {
            if (declaration.ExplicitName != null) return declaration.ExplicitName;
            var allocationId = AllocationIdentity(declaration.Id);
            // A physical-only signature/existential copy of the source declaration shares an explicit source name
            // only when it retained the source spelling. Compiler-owned bridges/wrappers with their own structured
            // name remain in that separate generated naming domain.
            if (explicitByAllocationId.TryGetValue(allocationId, out var explicitName)
                && (declaration.Id == allocationId || declaration.Name == declaration.SourceName))
                return explicitName;
            return declaration.Name;
        }

        var candidatesById = declarations
            .Select(declaration => (declaration.Id, Name: AllocatedName(declaration)))
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal);
        var physicalById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidates in candidatesById)
        {
            var names = candidates.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: declaration identity '{candidates.Key}' maps to multiple physical names: "
                    + string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal)));
            physicalById.Add(candidates.Key, names[0]);
        }
        // A non-owner-dependent existential slot is the same CLR contract as its source MethodDef, not a second
        // independently named implementation. Propagate an explicit source name to that slot; an unresolved collision
        // is rejected below instead of receiving an automatic declaration-id hash.
        var existentialMarker = PhysicalOnlySuffix + "|existential-slot:";
        foreach (var slot in declarations.Where(declaration =>
                     declaration.Id.Contains(existentialMarker, StringComparison.Ordinal)
                     && declaration.Name == declaration.SourceName))
        {
            var sourceId = AllocationIdentity(slot.Id);
            if (!physicalById.TryGetValue(sourceId, out var sourcePhysical)
                || !physicalById.TryGetValue(slot.Id, out var slotPhysical))
                continue;
            if (sourcePhysical != slot.SourceName && slotPhysical == slot.SourceName)
                physicalById[slot.Id] = sourcePhysical;
        }
        // CLR MethodDef collisions are owner-local, but dll2klib merges file facades back into one Kotlin package.
        // Preserve semantic signatures for a same-package/source-name pair that shares a physical signature across
        // different facades as well; otherwise the reverse projection can collapse Map and MutableMap to one type
        // before FIR has a chance to select either declaration.
        foreach (var group in declarations
            .Where(declaration => declaration.Package != null)
            .GroupBy(declaration => (declaration.Package, declaration.SourceName, declaration.Sig)))
        {
            var owners = group.Select(declaration => declaration.Owner).Distinct(StringComparer.Ordinal).ToArray();
            var ids = group.Select(declaration => declaration.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length < 2 || ids.Length < 2) continue;
            foreach (var id in ids) carrierIds.Add(id);
        }

        var finalDeclarations = allDeclarations.Select(declaration =>
        {
            var finalName = declaration.Id != null
                ? physicalById.TryGetValue(declaration.Id, out var allocated)
                    ? allocated
                    : throw new InvalidOperationException(
                        $"bir2cir: declaration identity '{declaration.Id}' has no physical allocation")
                : declaration.Name;
            return (Declaration: declaration, FinalName: finalName);
        });
        var collisionDiagnostics = new List<string>();
        foreach (var collision in finalDeclarations
            .GroupBy(candidate => (candidate.Declaration.Owner, candidate.FinalName, candidate.Declaration.Sig)))
        {
            // Shared synthesized types are projected into each per-file collision root and hoisted/deduplicated later.
            // Count that repeated generated declaration once, while retaining every independently authored method.
            var collisionCandidates = collision.Where(candidate => !candidate.Declaration.Generated)
                .Concat(collision.Where(candidate => candidate.Declaration.Generated).Take(1))
                .ToArray();
            if (collisionCandidates.Length < 2) continue;
            var parts = collision.Key.Sig.Split('|', 2);
            var arity = parts[0];
            var parameters = parts.Length == 2 ? parts[1] : "";
            var declarationsText = string.Join(", ", collisionCandidates.Select(candidate =>
                candidate.Declaration.Generated
                    ? $"compiler-generated '{candidate.Declaration.Name}'"
                    : $"Kotlin declaration '{candidate.Declaration.SourceName}'" +
                      (candidate.Declaration.Id == null ? " (open/override family)" : "")));
            collisionDiagnostics.Add(
                $"CLR physical signature collision at " +
                $"'{collision.Key.Owner}.{collision.Key.FinalName}' (generic arity {arity}; parameters [{parameters}]): " +
                $"{declarationsText}. Assign a distinct @ClrName to an independently allocatable user declaration; " +
                "declaration order and automatic hash suffixes are not used");
        }
        if (collisionDiagnostics.Count != 0)
            throw new InvalidOperationException(
                "bir2cir: unresolved CLR physical signature collisions:" + Environment.NewLine +
                string.Join(Environment.NewLine, collisionDiagnostics));
        semanticCarrierIds = carrierIds;
        return physicalById;
    }

    // Phase-3 representation passes may synthesize additional MethodDefs after source declaration allocation. Check
    // the actual final emission unit as a whole so a generated bridge can neither duplicate another generated bridge
    // nor silently collide with an explicitly named user declaration. This is validation only: no late renaming is
    // allowed because every call/MethodImpl/property association has already been physically bound.
    public static void ValidateFinalPhysicalNames(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.OfType<JsonObject>().ToList();
        var declarations = new List<(string Owner, string Name, string Sig,
            bool GeneratedOwner, bool GeneratedMethod)>();
        var properties = new List<(string Owner, string Name, string Sig)>();

        void CollectMethods(JsonObject owner, string ownerName, bool generatedOwner)
        {
            if (owner["ctors"] is JsonArray constructors)
                foreach (var constructor in constructors.OfType<JsonObject>())
                    declarations.Add((ownerName, ".ctor", PhysicalSignature(constructor),
                        generatedOwner, Bool(constructor["generated"])));
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Str(method["name"]) is string name)
                        declarations.Add((ownerName, name, PhysicalSignature(method),
                            generatedOwner, Bool(method["generated"])));
            if (owner["properties"] is JsonArray propertyRows)
                foreach (var property in propertyRows.OfType<JsonObject>())
                    if (Str(property["name"]) is string name)
                        properties.Add((ownerName, name, PhysicalPropertySignature(property)));
        }

        foreach (var root in rootList)
            // ilemit merges equal file facades and emits every method from every contributing CIR root. A root is
            // therefore never a generated TypeDef occurrence eligible for the first-wins rule below.
            CollectMethods(root, Str(root["fileClass"]) ?? "<file>", generatedOwner: false);

        // Match ilemit's owner pass before validating methods. File facades are defined first. A later generated
        // TypeDef is skipped whenever any prior owner already occupies its name; only methods on the emitted first
        // occurrence belong to the final MethodDef table. An ordinary duplicate TypeDef is a separate malformed-CIR
        // failure in ilemit, so it contributes no second MethodDef table here.
        var definedOwners = new HashSet<string>(rootList
            .Where(root => (root["methods"] as JsonArray)?.Count > 0
                           || (root["fields"] as JsonArray)?.Count > 0)
            .Select(root => Str(root["fileClass"]) ?? "<file>"), StringComparer.Ordinal);
        foreach (var root in rootList)
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                {
                    var ownerName = Str(type["name"]) ?? "<anonymous>";
                    if (!definedOwners.Add(ownerName)) continue;
                    CollectMethods(type, ownerName, Bool(type["generated"]));
                }

        var collisions = declarations
            .GroupBy(declaration => (declaration.Owner, declaration.Name, declaration.Sig))
            .Select(group => new
            {
                group.Key,
                Candidates = group.ToArray(),
            })
            .Where(group => group.Candidates.Length > 1)
            .Select(group =>
            {
                var parts = group.Key.Sig.Split('|', 2);
                var arity = parts[0];
                var parameters = parts.Length == 2 ? parts[1] : "";
                var kinds = string.Join(", ", group.Candidates.Select(declaration =>
                    declaration.GeneratedOwner || declaration.GeneratedMethod
                        ? "compiler-generated declaration"
                        : "user declaration"));
                return $"CLR physical signature collision at '{group.Key.Owner}.{group.Key.Name}' " +
                       $"(generic arity {arity}; parameters [{parameters}]): {kinds}";
            })
            .ToArray();
        if (collisions.Length != 0)
            throw new InvalidOperationException(
                "bir2cir: final CIR contains duplicate MethodDef signatures; late collision repair is forbidden:" +
                Environment.NewLine + string.Join(Environment.NewLine, collisions));

        // Equal file facades are separate CIR roots until ilemit merges them. Validate the Property table with the
        // same physical-owner model as methods so a duplicate row cannot hide merely by living in a sibling root.
        var propertyCollisions = properties
            .GroupBy(property => (property.Owner, property.Name, property.Sig))
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"CLR physical PropertyDef collision at '{group.Key.Owner}.{group.Key.Name}' " +
                $"(signature [{group.Key.Sig}])")
            .ToArray();
        if (propertyCollisions.Length != 0)
            throw new InvalidOperationException(
                "bir2cir: final CIR contains duplicate PropertyDef signatures; late collision repair is forbidden:" +
                Environment.NewLine + string.Join(Environment.NewLine, propertyCollisions));
    }

    public static void ApplyLocal(
        IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> physicalById,
        IReadOnlySet<string> semanticCarrierIds,
        IReadOnlyDictionary<string, JsonObject> semanticSignatures,
        ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();

        static List<JsonObject> Collect(IEnumerable<JsonNode> sourceRoots)
        {
            var result = new List<JsonObject>();
            void WalkOwner(JsonObject owner)
            {
                if (owner["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                        if (Str(method[Key]) != null) result.Add(method);
                if (owner["types"] is JsonArray types)
                    foreach (var type in types.OfType<JsonObject>()) WalkOwner(type);
            }
            foreach (var root in sourceRoots.OfType<JsonObject>()) WalkOwner(root);
            return result;
        }

        var declarations = Collect(rootList);

        foreach (var declaration in declarations)
        {
            var id = Str(declaration[Key])!;
            if (!physicalById.TryGetValue(id, out var physical))
                throw new InvalidOperationException(
                    $"bir2cir: declaration identity '{id}' is absent from the CLR collision projection");
            declaration["name"] = physical;
            if (Str(declaration["extensionCoreDeclarationId"]) is string coreId)
            {
                if (!physicalById.TryGetValue(coreId, out var corePhysical))
                    throw new InvalidOperationException(
                        $"bir2cir: companion-extension wrapper core identity '{coreId}' has no physical name");
                RoundtripMetadata.StampExtensionCore(declaration, corePhysical);
                declaration.Remove("extensionCoreDeclarationId");
            }
            // C# 14 companion-extension signature declarations and generic wrappers are physical ABI artifacts,
            // not additional Kotlin declarations. They participate in collision allocation, then discard the
            // synthetic key before round-trip metadata is stamped.
            if (id.Contains(PhysicalOnlySuffix, StringComparison.Ordinal))
            {
                declaration.Remove(Key);
                declaration.Remove(SemanticSignatureKey);
                declaration.Remove("declarationSourceName");
                continue;
            }
            // SuspendColdLowering derives a private implementation entry from the selected source declaration. Its
            // `|cold` identity is needed to bind inline/generated calls across modules, but it is not another Kotlin
            // declaration. Keep the two-field physical carrier for ReferenceMetadataIndex; CompilerGenerated keeps
            // dll2klib from projecting the entry into KLIB, and no semantic signature belongs on it.
            if (id.EndsWith("|cold", StringComparison.Ordinal))
            {
                declaration.Remove(SemanticSignatureKey);
                continue;
            }
            // Carry the semantic signature when CLR erasure collapsed declarations under one MethodDef owner, or when
            // the same collapse occurs only after dll2klib merges distinct file facades back into one Kotlin package.
            // Ordinary declarations retain the established specialized metadata paths for nesting/context/companions.
            if (semanticCarrierIds.Contains(id))
                declaration[SemanticSignatureKey] = semanticSignatures.TryGetValue(id, out var signature)
                    ? signature.DeepClone()
                    : throw new InvalidOperationException(
                        $"declaration identity '{id}' has no preserved semantic signature");
            else declaration.Remove(SemanticSignatureKey);
        }

        void Rewrite(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var child in obj.Select(kv => kv.Value).ToList()) if (child != null) Rewrite(child);
                if (Str(obj["unsafeTargetDeclarationId"]) is string targetId)
                {
                    if (!physicalById.TryGetValue(targetId, out var targetPhysical)
                        && !refs.TryDeclarationIdentity(
                            targetId, out targetPhysical, out _, out _, out _))
                        throw new InvalidOperationException(
                            $"bir2cir: unresolved UnsafeAccessor target declaration identity '{targetId}'");
                    RewriteUnsafeAccessorTarget(obj, targetPhysical);
                    obj.Remove("unsafeTargetDeclarationId");
                }
                if (Str(obj[Key]) is not string id || !physicalById.TryGetValue(id, out var physical)) return;
                if (Str(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall"
                    or "newDelegate" or "newBoundDelegate")
                {
                    obj["method"] = physical;
                    // KotlinPropertyAccessors has already allocated the exact local accessor declaration. Leaving
                    // the semantic role beside its physical MethodDef name would make late member resolution apply
                    // the one-way get_/set_ projection a second time (`get_get_x`).
                    obj.Remove("prop");
                    obj.Remove(Key);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList()) if (child != null) Rewrite(child);
        }
        foreach (var root in rootList) Rewrite(root);

        void RejectUnboundUse(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) is ("callStatic" or "callInstance" or "constrainedCall"
                    or "newDelegate" or "newBoundDelegate") && Str(obj[Key]) is string id)
                    throw new InvalidOperationException($"bir2cir: unresolved frontend declaration identity '{id}'");
                foreach (var child in obj.Select(kv => kv.Value).ToList()) if (child != null) RejectUnboundUse(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array.ToList()) if (child != null) RejectUnboundUse(child);
        }
        foreach (var root in rootList) RejectUnboundUse(root);
    }

    static void RewriteUnsafeAccessorTarget(JsonObject method, string physicalName)
    {
        if (method["attrs"] is not JsonArray attrs)
            throw new InvalidOperationException("bir2cir: declaration-bound UnsafeAccessor has no attribute list");
        foreach (var attr in attrs.OfType<JsonObject>())
        {
            if (TypeJson.OwnerName(attr["attr"]) != "System.Runtime.CompilerServices.UnsafeAccessorAttribute"
                || attr["namedArgs"] is not JsonArray named) continue;
            foreach (var arg in named.OfType<JsonObject>())
                if (Str(arg["name"]) == "Name" && arg["value"] is JsonObject value)
                {
                    value["value"] = physicalName;
                    return;
                }
        }
        throw new InvalidOperationException("bir2cir: declaration-bound UnsafeAccessor has no Name argument");
    }

    static string PhysicalSignature(JsonObject method)
    {
        var arity = (method["typeParams"] as JsonArray)?.Count ?? 0;
        var parameters = method["params"] is JsonArray ps
            ? string.Join(";", ps.OfType<JsonObject>().Select(p => PhysicalTypeSignature(
                TypeJson.Read(p["type"])
                ?? throw new InvalidOperationException("bir2cir: declaration identity has an untyped physical parameter"))))
            : "";
        return arity + "|" + parameters;
    }

    static string PhysicalPropertySignature(JsonObject property)
    {
        var propertyType = PhysicalPropertyTypeSignature(TypeJson.Read(property["type"])
            ?? throw new InvalidOperationException("bir2cir: final PropertyDef has no physical type"));
        IEnumerable<JsonNode> indexParameters = property["getSig"] is JsonArray getSig
            ? getSig
            : property["setSig"] is JsonArray setSig
                ? setSig.Take(Math.Max(0, setSig.Count - 1))
                : throw new InvalidOperationException("bir2cir: final PropertyDef has no accessor signature");
        return propertyType + "|" + string.Join(";", indexParameters.Select(parameter =>
            PhysicalPropertyTypeSignature(TypeJson.Read(parameter)
                ?? throw new InvalidOperationException(
                    "bir2cir: final PropertyDef has an untyped physical index parameter"))));
    }

    // A declaration is allocated only after BirTypeLowering, so this key describes the actual CLI parameter types,
    // not their remaining JSON spellings. In particular, suspend-function values erase to the shorthand `object`
    // while an Any parameter normally arrives as `System.Object`; raw JSON comparison would miss that collision and
    // leave two MethodDefs with one CLR signature. Keep this nominal projection aligned with ilemit's MapType rules.
    internal static string PhysicalTypeSignature(TypeNode type) => type switch
    {
        TypeNode.Fqn { Args: null } f => PhysicalTypeName(f.Name),
        TypeNode.Fqn f => PhysicalTypeName(f.Name) + "[" +
            string.Join(",", f.Args!.Select(PhysicalTypeSignature)) + "]",
        // Generic-parameter scope and index are part of an open CLR MethodDef signature (`!0` and `!1`, or `!0` and
        // `!!0`, are distinct). Constructing an owner with equal arguments does not merge its MethodDef rows.
        TypeNode.Tv tv => "gp:" + tv.Scope + ":" + tv.I,
        TypeNode.Fn fn => PhysicalDelegateSignature(fn),
        TypeNode.Nullable n => "System.Nullable[" + PhysicalTypeSignature(n.Of) + "]",
        TypeNode.Oblivious o => PhysicalTypeSignature(o.Of),
        TypeNode.Array a => "array:" + PhysicalTypeSignature(a.Elem),
        TypeNode.ByRef b => "byref:" + PhysicalTypeSignature(b.Of),
        TypeNode.Star => throw new InvalidOperationException(
            "bir2cir: unresolved star projection reached final CLR MethodDef signature allocation"),
        _ => throw new InvalidOperationException(
            $"bir2cir: unsupported type node '{type.GetType().Name}' reached final CLR MethodDef signature allocation"),
    };

    // A PropertyDef signature retains the declaring type-parameter scope and index (`!0` vs `!1`). Unlike a
    // call-side MethodDef key, it must not collapse those slots merely because a closed owner could substitute the
    // same concrete type for both. The associated accessor MethodDefs already have independently allocated names,
    // and MethodSemantics binds the Property row to that exact declaration.
    internal static string PhysicalPropertyTypeSignature(TypeNode type) => PhysicalTypeSignature(type);

    static string PhysicalDelegateSignature(TypeNode.Fn fn)
    {
        var args = fn.DelegateParams.Select(PhysicalTypeSignature).ToList();
        var returnsVoid = PhysicalTypeSignature(fn.Ret) == "System.Void";
        var family = fn.Clr ?? throw new InvalidOperationException(
            "bir2cir: declaration identity physical signature contains an unresolved function type");
        if (!returnsVoid) args.Add(PhysicalTypeSignature(fn.Ret));
        return family switch
        {
            "System.Action" when returnsVoid && args.Count == 0 => "System.Action",
            "System.Action" when returnsVoid => "System.Action[" + string.Join(",", args) + "]",
            "System.Func" when !returnsVoid => "System.Func[" + string.Join(",", args) + "]",
            "DotKt.Runtime.CompilerServices.KAction" when returnsVoid =>
                "DotKt.Runtime.CompilerServices.KAction[" + string.Join(",", args) + "]",
            "DotKt.Runtime.CompilerServices.KFunc" when !returnsVoid =>
                "DotKt.Runtime.CompilerServices.KFunc[" + string.Join(",", args) + "]",
            _ => throw new InvalidOperationException(
                $"bir2cir: declaration identity physical signature has invalid delegate family '{family}'"),
        };
    }

    static string PhysicalTypeName(string name) => name switch
    {
        "void" or "kotlin.Unit" => "System.Void",
        "int" or "kotlin.Int" => "System.Int32",
        "long" or "kotlin.Long" => "System.Int64",
        "short" or "kotlin.Short" => "System.Int16",
        "sbyte" or "kotlin.Byte" => "System.SByte",
        "double" or "kotlin.Double" => "System.Double",
        "float" or "kotlin.Float" => "System.Single",
        "bool" or "kotlin.Boolean" => "System.Boolean",
        "char" or "kotlin.Char" => "System.Char",
        "string" or "kotlin.String" => "System.String",
        "object" or "kotlin.Any" or "kotlin.Nothing" => "System.Object",
        "uint" or "kotlin.UInt" => "System.UInt32",
        "ulong" or "kotlin.ULong" => "System.UInt64",
        "byte" or "kotlin.UByte" => "System.Byte",
        "ushort" or "kotlin.UShort" => "System.UInt16",
        _ => name,
    };

    internal static string StableSuffix(string id)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    static string AllocationIdentity(string id)
    {
        var marker = id.IndexOf(PhysicalOnlySuffix, StringComparison.Ordinal);
        return marker < 0 ? id : id[..marker];
    }

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    static bool Bool(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;

    static void ValidateExplicitName(string name, string owner, string sourceName)
    {
        if (name.Length == 0)
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name must not be empty");
        if (name.IndexOf('\0') >= 0)
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name must not contain NUL");
        if (name is ".ctor" or ".cctor")
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': '{name}' is a reserved CLR constructor name");
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(name);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name is not valid Unicode");
        }
    }
}
