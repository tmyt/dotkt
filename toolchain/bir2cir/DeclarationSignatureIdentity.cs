using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin overloads can have identical CLR value signatures while
// their nominal parameter types or generic parameter bounds remain distinct.
// Keep the value/generic frame unchanged; state that distinction in modopt.
static class DeclarationSignatureIdentity
{
    const string BoundsKey = "genericParameterSignatureBounds";
    const string NominalKey = "nominalParameterSignatureIdentity";
    const string OverrideEdgesKey = "signatureOverrideDeclarations";
    internal const string AllocatedKey = "declarationSignatureAllocation";
    internal const string AllocationMetadataKey = "signatureAllocation";
    static readonly Dictionary<string, BirClassDefinition> Markers = new(StringComparer.Ordinal);
    static readonly HashSet<(bool Required, string Owner, int Arity)> ReferencedModifiers = new();
    static readonly Dictionary<string, List<(JsonObject Descriptor, bool IsInterface)>> PhysicalSlots = new(StringComparer.Ordinal);
    static string MarkerOwner;

    // The producer has already selected this implementation relation. Keep its declaration IDs through late
    // signature allocation even when the original value signatures allowed an implicit CLR interface match.
    internal static void RecordSelectedImplementationSlot(JsonObject implementation, JsonObject slot)
    {
        var id = Text(slot[DeclarationIdentityBinding.Key]);
        if (id == null) return;
        if (implementation[OverrideEdgesKey] is not JsonArray edges)
            implementation[OverrideEdgesKey] = edges = new JsonArray();
        if (!edges.Select(Text).Contains(id, StringComparer.Ordinal)) edges.Add(id);
    }

    // The physical override pass has already selected a native slot. Keep its exact descriptor in memory rather
    // than inventing a Kotlin identity or sending a physical owner frame through another semantic rewrite.
    internal static void RecordSelectedPhysicalSlot(JsonObject implementation, JsonObject descriptor, bool isInterface)
    {
        if (Text(implementation[DeclarationIdentityBinding.Key]) is not string id) return;
        if (!PhysicalSlots.TryGetValue(id, out var slots)) PhysicalSlots[id] = slots = new();
        if (!slots.Any(slot => slot.IsInterface == isInterface && JsonNode.DeepEquals(slot.Descriptor, descriptor)))
            slots.Add((descriptor.DeepClone().AsObject(), isInterface));
    }

    public static BirFileDeclaration[] Capture(IEnumerable<BirFileDeclaration> roots)
    {
        Markers.Clear();
        ReferencedModifiers.Clear();
        PhysicalSlots.Clear();
        BirGraphValue Walk(BirGraphValue node, string owner)
        {
            owner = node switch
            {
                BirClassDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                BirInterfaceDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                BirEnumDefinition { Methods.HasValue: true, Name.HasValue: true } type => type.Name.Value,
                _ => owner,
            };
            MarkerOwner = owner;
            node = node switch
            {
                BirFunctionDeclaration method => method with {
                    Params = CaptureParameters(method.Params, method.TypeParams),
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirConstructor constructor => constructor with { Params = CaptureParameters(constructor.Params, default) },
                BirClassDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirInterfaceDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirEnumDefinition type => type with { Params = CaptureParameters(type.Params, type.TypeParams) },
                BirInheritedClassMethod method => method with {
                    Params = CaptureParameters(method.Params, method.TypeParams),
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirInheritedDefaultAccessor accessor => accessor with {
                    SignatureOverrideDeclarations = accessor.Params.HasValue && accessor.Overrides.HasValue
                        ? OverrideIds(accessor.Overrides.Value) : accessor.SignatureOverrideDeclarations,
                },
                BirInheritedDefaultMethod method => method with {
                    SignatureOverrideDeclarations = method.Params.HasValue && method.Overrides.HasValue
                        ? OverrideIds(method.Overrides.Value) : method.SignatureOverrideDeclarations,
                },
                BirClosureDefinition closure => closure with { Params = CaptureParameters(closure.Params, closure.TypeParams) },
                BirNewSuspendLambda lambda => lambda with { Params = CaptureParameters(lambda.Params, lambda.TypeParams) },
                BirInlineLambda lambda => lambda with { Params = CaptureParameters(lambda.Params, default) },
                _ => node,
            };
            return node.RewriteChildren(child => Walk(child, owner));
        }
        var rootList = roots.Select(root => (BirFileDeclaration)Walk(root, root.FileClass.OrDefault())).ToArray();
        if (rootList.Length != 0)
            rootList[0] = rootList[0] with { Types = rootList[0].Types.OrEmpty().Concat(Markers.Values).ToArray() };
        return rootList;
    }

    static string[] OverrideIds(BirOverride[] overrides) => overrides.Select(edge => edge.DeclarationId.OrDefault())
        .Where(id => id != null).ToArray();

    static TypeNode[] CaptureBounds(TypeNode type, OptionalField<BirTypeParameter[]> variables) => MethodVariables(type)
        .Distinct().OrderBy(index => index).Where(index => index >= 0 && variables.HasValue && index < variables.Value.Length)
        .SelectMany(index => variables.Value[index].Constraints.OrEmpty().Select(Encode)).ToArray();

    static OptionalField<BirParameter[]> CaptureParameters(OptionalField<BirParameter[]> parameters,
        OptionalField<BirTypeParameter[]> variables) => !parameters.HasValue ? parameters
        : parameters.Value.Select(parameter => {
            if (!parameter.Type.HasValue) return parameter;
            var nominal = Encode(parameter.Type.Value);
            var bounds = CaptureBounds(parameter.Type.Value, variables);
            return parameter with { NominalParameterSignatureIdentity = nominal,
                GenericParameterSignatureBounds = bounds.Length == 0 ? parameter.GenericParameterSignatureBounds : bounds };
        }).ToArray();

    static OptionalField<BirLambdaParameter[]> CaptureParameters(OptionalField<BirLambdaParameter[]> parameters,
        OptionalField<BirTypeParameter[]> variables) => !parameters.HasValue ? parameters
        : parameters.Value.Select(parameter => {
            if (!parameter.Type.HasValue) return parameter;
            var nominal = Encode(parameter.Type.Value);
            var bounds = CaptureBounds(parameter.Type.Value, variables);
            return parameter with { NominalParameterSignatureIdentity = nominal,
                GenericParameterSignatureBounds = bounds.Length == 0 ? parameter.GenericParameterSignatureBounds : bounds };
        }).ToArray();

    public static void RemoveCapturedFacts(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(BoundsKey);
            obj.Remove(NominalKey);
            obj.Remove(OverrideEdgesKey);
            obj.Remove(AllocatedKey);
            foreach (var child in obj.Select(pair => pair.Value)) RemoveCapturedFacts(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemoveCapturedFacts(child);
    }

    internal static void RewriteParameterFacts(JsonObject parameter, Func<TypeNode, TypeNode> rewrite)
    {
        if (TypeJson.Read(parameter[NominalKey]) is TypeNode nominal)
            parameter[NominalKey] = TypeJson.Write(rewrite(nominal));
        if (parameter[BoundsKey] is JsonArray bounds)
            parameter[BoundsKey] = new JsonArray(bounds.Select(TypeJson.Read)
                .Select(rewrite).Select(TypeJson.Write).ToArray());
    }

    public static IReadOnlyDictionary<string, JsonArray> Plan(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs = null)
    {
        var methods = new List<(string Owner, string Name, JsonObject Method)>();
        void Collect(JsonObject owner, string name)
        {
            foreach (var method in (owner["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                methods.Add((name, Text(method[DeclarationIdentityBinding.ExplicitNameKey]) ?? Text(method["name"]), method));
            foreach (var constructor in (owner["ctors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                methods.Add((name, ".ctor", constructor));
            foreach (var type in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Collect(type, Text(type["name"]));
        }
        foreach (var root in roots.OfType<JsonObject>()) Collect(root, Text(root["fileClass"]));
        var sourceMethods = methods.Where(item => Text(item.Method[DeclarationIdentityBinding.Key]) != null)
            .GroupBy(item => Text(item.Method[DeclarationIdentityBinding.Key]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Method, StringComparer.Ordinal);
        var methodOwners = methods.Where(item => Text(item.Method[DeclarationIdentityBinding.Key]) != null)
            .GroupBy(item => Text(item.Method[DeclarationIdentityBinding.Key]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Owner, StringComparer.Ordinal);
        var definitions = SupertypeGraph.Collect(roots);
        bool Eligible(JsonObject method)
        {
            if (Authored(method)) return true;
            var id = Text(method[DeclarationIdentityBinding.Key]);
            return id?.EndsWith("|cold", StringComparison.Ordinal) == true
                && sourceMethods.TryGetValue(id[..^5], out var source) && Authored(source);
        }
        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        static string PhysicalParameters(JsonArray parameters) => string.Join(";", parameters
            .Select(parameter => DeclarationIdentityBinding.PhysicalTypeSignature(TypeJson.Read(parameter))));
        foreach (var group in methods.GroupBy(item =>
                     (item.Owner, item.Name, Signature: DeclarationIdentityBinding.PhysicalSignature(item.Method))))
        {
            var candidates = group.ToArray();
            if (candidates.Length < 2 || candidates.Any(item => !Eligible(item.Method))) continue;
            var projected = candidates.Select(item => (item.Method, Parameters: Project(item.Method))).ToArray();
            if (projected.Select(item => PhysicalParameters(item.Parameters)).Distinct(StringComparer.Ordinal).Count()
                != candidates.Length)
                projected = candidates.Select(item => (item.Method, Parameters: Project(item.Method, nominal: true))).ToArray();
            if (projected.Select(item => PhysicalParameters(item.Parameters)).Distinct(StringComparer.Ordinal).Count()
                != candidates.Length) continue;
            foreach (var (method, parameters) in projected)
                result.Add(Text(method[DeclarationIdentityBinding.Key]), parameters);
        }
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        void ResolveOverride(string id)
        {
            if (!resolved.Add(id) || !sourceMethods.TryGetValue(id, out var method)
                || method[OverrideEdgesKey] is not JsonArray edges) return;
            foreach (var target in edges.Select(Text).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (target == null) continue;
                ResolveOverride(target);
                string selectedOwnerName;
                if (result.TryGetValue(target, out var signature)) selectedOwnerName = methodOwners[target];
                else if (refs?.TryDeclarationSignatureSlot(target, out var referenced) == true
                    && referenced.SignatureAllocated)
                {
                    // A referenced override family already owns its exact signature discriminator. Reuse it just
                    // as for a local ancestor instead of allocating another identity and bridging the same slot twice.
                    signature = new JsonArray(referenced.Parameters.Select(TypeJson.Write).ToArray());
                    foreach (var parameter in referenced.Parameters)
                        for (var type = parameter; type is TypeNode.Mod modifier; type = modifier.Of)
                            if (modifier.M is TypeNode.Fqn marker)
                                ReferencedModifiers.Add((modifier.Req, marker.Name, marker.Args?.Length ?? 0));
                    selectedOwnerName = referenced.Owner;
                }
                else continue;
                var selectedOwner = SelectedLocalOwner(definitions[methodOwners[id]], selectedOwnerName, definitions, refs);
                var arguments = selectedOwner.Args ?? Array.Empty<TypeNode>();
                result[id] = new JsonArray(signature.Select(TypeJson.Read)
                    .Select(type => TypeJson.Write(SupertypeGraph.SubstOwnerTvs(type, arguments))).ToArray());
                break;
            }
        }
        foreach (var id in sourceMethods.Keys) ResolveOverride(id);
        return result;
    }

    // A signature modifier distinguishes a declaration, not a new value type. An override can therefore retain
    // its own declaration identity while filling the exact selected ancestor slot through an explicit MethodImpl.
    // The frontend's override declaration ID selects that slot before its Object parameters become ambiguous.
    public static void BindSelectedOverrides(IEnumerable<JsonNode> roots, IReadOnlyDictionary<string, JsonArray> plan,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var definitions = SupertypeGraph.Collect(roots);
        var declarations = definitions.Values.SelectMany(owner => owner.Methods.OfType<JsonObject>()
                .Where(method => Text(method[DeclarationIdentityBinding.Key]) != null)
                .Select(method => (Owner: owner, Method: method)))
            .ToDictionary(item => Text(item.Method[DeclarationIdentityBinding.Key]), StringComparer.Ordinal);
        void BindSlot(SupertypeGraph.Def owner, JsonObject method, JsonObject descriptor, bool isInterface, string slotKey)
        {
            var key = isInterface ? "clrInterfaceImpls" : "clrBaseImpls";
            if (method[key] is not JsonArray descriptors) method[key] = descriptors = new JsonArray();
            var name = Text(descriptor["member"]);
            // Retarget only this already-selected relation. Remove its old body mapping before a new exact
            // signature body takes ownership; retaining both would map a changed body to the native plain slot.
            for (var index = descriptors.Count - 1; index >= 0; index--)
                if (JsonNode.DeepEquals(descriptors[index]?["owner"], descriptor["owner"])
                    && Text(descriptors[index]?["member"]) == name
                    && JsonNode.DeepEquals(descriptors[index]?["arity"], descriptor["arity"]))
                    descriptors.RemoveAt(index);
            var targetParameters = descriptor["params"].AsArray();
            var bodyParameters = new JsonArray(((JsonArray)method["params"]).OfType<JsonObject>()
                .Select(parameter => parameter["type"].DeepClone()).ToArray());
            if (!targetParameters.Select(parameter => DeclarationIdentityBinding.PhysicalTypeSignature(TypeJson.Read(parameter)))
                    .SequenceEqual(bodyParameters.Select(parameter =>
                        DeclarationIdentityBinding.PhysicalTypeSignature(TypeJson.Read(parameter)))))
            {
                var bridgeName = name + "$signature$" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Text(method[DeclarationIdentityBinding.Key]) + "|" + slotKey)));
                var bridge = KotlinOverrideSlotBridge.BuildSignatureBridge(owner, method,
                    targetParameters.Select(TypeJson.Read).ToArray(), TypeJson.Read(descriptor["ret"]),
                    bridgeName, isValue, refs);
                owner.Methods.Add(bridge);
                bridge[key] = descriptors = new JsonArray();
            }
            descriptors.Add(descriptor.DeepClone());
            method["virtual"] = true;
        }
        foreach (var owner in definitions.Values)
            foreach (var method in owner.Methods.OfType<JsonObject>().ToArray())
            {
                var overrides = method[OverrideEdgesKey] as JsonArray ?? new JsonArray();
                foreach (var id in overrides.Select(Text))
                {
                    if (id == null) continue;
                    string selectedOwnerName, name;
                    int arity;
                    bool selectedInterface;
                    TypeNode[] openParameters;
                    TypeNode openReturn;
                    var referencedAllocation = false;
                    if (declarations.TryGetValue(id, out var selected))
                    {
                        selectedOwnerName = selected.Owner.Name;
                        selectedInterface = selected.Owner.Kind == "interface";
                        name = Text(selected.Method["name"]);
                        arity = (selected.Method["typeParams"] as JsonArray)?.Count ?? 0;
                        openParameters = ((JsonArray)selected.Method["params"]).OfType<JsonObject>()
                            .Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
                        openReturn = TypeJson.Read(selected.Method["ret"]);
                    }
                    else if (refs?.TryDeclarationSignatureSlot(id, out var referenced) == true)
                    {
                        selectedOwnerName = referenced.Owner;
                        selectedInterface = referenced.IsInterface;
                        name = referenced.Name;
                        arity = referenced.Arity;
                        openParameters = referenced.Parameters;
                        openReturn = referenced.Return;
                        referencedAllocation = referenced.SignatureAllocated;
                    }
                    else continue;
                    var bodyAllocated = Text(method[DeclarationIdentityBinding.Key]) is string implementationId
                        && plan.ContainsKey(implementationId);
                    if (!bodyAllocated && !plan.ContainsKey(id) && !referencedAllocation) continue;
                    var selectedOwner = SelectedLocalOwner(owner, selectedOwnerName, definitions, refs);
                    var args = selectedOwner.Args ?? Array.Empty<TypeNode>();
                    var targetParameters = new JsonArray(openParameters
                        .Select(type => TypeJson.Write(SupertypeGraph.SubstOwnerTvs(type, args))).ToArray());
                    var targetReturn = SupertypeGraph.SubstOwnerTvs(openReturn, args);
                    BindSlot(owner, method, new JsonObject {
                        ["owner"] = TypeJson.Write(selectedOwner), ["member"] = name, ["arity"] = arity,
                        ["params"] = targetParameters,
                        ["ret"] = TypeJson.Write(targetReturn),
                    }, selectedInterface, id);
                }
                if (Text(method[DeclarationIdentityBinding.Key]) is string sourceId && plan.ContainsKey(sourceId)
                    && PhysicalSlots.TryGetValue(sourceId, out var selectedPhysicalSlots))
                    foreach (var slot in selectedPhysicalSlots)
                        BindSlot(owner, method, slot.Descriptor, slot.IsInterface, slot.Descriptor.ToJsonString());
                method.Remove(OverrideEdgesKey);
            }
    }

    static TypeNode.Fqn SelectedLocalOwner(SupertypeGraph.Def source, string selectedOwner,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs) =>
        SupertypeGraph.Reachable(source, definitions, refs).Select(edge => edge.spec)
            .Where(spec => spec.Name == selectedOwner).Distinct().Single();

    public static void Apply(IEnumerable<JsonNode> roots, IReadOnlyDictionary<string, JsonArray> plan)
    {
        var rootList = roots.ToArray();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                // UnsafeAccessor's runtime matcher includes custom modifiers. Carry the selected local target's
                // allocated identity to its exact extern signature, excluding the synthetic owner argument.
                if (Text(obj["unsafeTargetDeclarationId"] ?? obj["unsafeSignatureTargetDeclarationId"]) is string targetId
                    && plan.TryGetValue(targetId, out var targetTypes) && obj["params"] is JsonArray accessorParameters)
                {
                    var offset = accessorParameters.Count - targetTypes.Count;
                    if (offset is < 0 or > 1)
                        throw new InvalidOperationException("UnsafeAccessor signature differs from its selected declaration");
                    for (var index = 0; index < targetTypes.Count; index++)
                        accessorParameters[index + offset]["type"] = TypeJson.Write(WithIdentityModifiers(
                            TypeJson.Read(targetTypes[index]), TypeJson.Read(accessorParameters[index + offset]["type"])));
                }
                obj.Remove("unsafeSignatureTargetDeclarationId");
                if (Text(obj[DeclarationIdentityBinding.Key]) is string id && plan.TryGetValue(id, out var types))
                {
                    if (obj["params"] is JsonArray parameters)
                    {
                        obj[AllocatedKey] = true;
                        if (parameters.Count != types.Count)
                            throw new InvalidOperationException("Generic signature allocation changed parameter arity");
                        for (var index = 0; index < parameters.Count; index++)
                            parameters[index]["type"] = TypeJson.Write(WithIdentityModifiers(
                                TypeJson.Read(types[index]), TypeJson.Read(parameters[index]["type"])));
                    }
                    else if (Text(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall"
                        or "newDelegate" or "newBoundDelegate")
                        obj["calleeParams"] = types.DeepClone();
                }
                if (Text(obj["constructorDeclarationId"]) is string constructorId && plan.TryGetValue(constructorId, out var constructorTypes))
                    obj["memberSignature"] = constructorTypes.DeepClone();
                if (Text(obj["delegationDeclarationId"]) is string delegationId && plan.TryGetValue(delegationId, out var delegationTypes))
                    obj["delegationSig"] = delegationTypes.DeepClone();
                obj.Remove(BoundsKey);
                obj.Remove(NominalKey);
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Walk(child);
        }
        foreach (var root in rootList) Walk(root);
        var usedMarkers = new HashSet<string>(StringComparer.Ordinal);
        void CollectMarkers(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Text(obj["name"]) is string name && Markers.ContainsKey(name)) usedMarkers.Add(name);
                foreach (var child in obj.Select(pair => pair.Value)) CollectMarkers(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) CollectMarkers(child);
        }
        foreach (var signature in plan.Values) CollectMarkers(signature);
        foreach (var root in rootList.OfType<JsonObject>())
            if (root["types"] is JsonArray types)
                for (var index = types.Count - 1; index >= 0; index--)
                    if (Text(types[index]?["name"]) is string name && Markers.ContainsKey(name)
                        && !usedMarkers.Contains(name)) types.RemoveAt(index);
    }

    static TypeNode WithIdentityModifiers(TypeNode signature, TypeNode valueType)
    {
        if (signature is not TypeNode.Mod modifier) return valueType;
        if (valueType is TypeNode.Mod existing && existing.Req == modifier.Req && existing.M == modifier.M)
            return new TypeNode.Mod(existing.Req, existing.M, WithIdentityModifiers(modifier.Of, existing.Of));
        var underlying = WithIdentityModifiers(modifier.Of, valueType);
        return modifier.M is TypeNode.Fqn marker && (Markers.ContainsKey(marker.Name)
                || ReferencedModifiers.Contains((modifier.Req, marker.Name, marker.Args?.Length ?? 0)))
            ? new TypeNode.Mod(modifier.Req, modifier.M, underlying) : underlying;
    }

    static JsonArray Project(JsonObject method, bool nominal = false)
    {
        var projected = new JsonArray();
        foreach (var parameter in ((JsonArray)method["params"]).OfType<JsonObject>())
        {
            var type = TypeJson.Read(parameter["type"]);
            if (parameter[BoundsKey] is JsonArray constraints)
                foreach (var bound in constraints.Select(TypeJson.Read)
                             .Where(bound => bound is not TypeNode.Fqn { Name: "object" or "System.Object" })
                             .OrderBy(bound => TypeJson.Write(bound).ToJsonString(), StringComparer.Ordinal))
                    type = new TypeNode.Mod(false, bound, type);
            if (nominal && parameter[NominalKey] is JsonNode identity)
                type = new TypeNode.Mod(false, TypeJson.Read(identity), type);
            projected.Add(TypeJson.Write(type));
        }
        return projected;
    }

    static IEnumerable<int> MethodVariables(TypeNode type) => type switch
    {
        TypeNode.Tv { Scope: "method" } variable => new[] { variable.I },
        TypeNode.Nullable nullable => MethodVariables(nullable.Of),
        TypeNode.Oblivious oblivious => MethodVariables(oblivious.Of),
        TypeNode.Projection projection => MethodVariables(projection.Of),
        TypeNode.Fqn named => named.Args?.SelectMany(MethodVariables) ?? Enumerable.Empty<int>(),
        TypeNode.Array array => MethodVariables(array.Elem),
        TypeNode.ByRef reference => MethodVariables(reference.Of),
        TypeNode.Ptr pointer => MethodVariables(pointer.Of),
        TypeNode.Mod modifier => MethodVariables(modifier.Of),
        TypeNode.Fn function => MethodVariables(function.Ret).Concat(function.Params.SelectMany(MethodVariables))
            .Concat(MethodVariables(function.Recv)).Concat(function.Ctx?.SelectMany(MethodVariables) ?? Enumerable.Empty<int>()),
        _ => Enumerable.Empty<int>(),
    };

    static TypeNode Encode(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => Encode(nullable.Of),
        TypeNode.Oblivious oblivious => Encode(oblivious.Of),
        TypeNode.Tv variable => Marker("variable", new TypeNode[] { variable }),
        TypeNode.Fqn named => Marker("nominal:" + named.Name,
            named.Args?.Select(Encode).ToArray() ?? Array.Empty<TypeNode>()),
        TypeNode.Projection projection => Marker("projection:" + projection.Variance, new[] { Encode(projection.Of) }),
        TypeNode.Star => Marker("star", Array.Empty<TypeNode>()),
        TypeNode.Array array => Marker("array:" + array.Rank + ":" + array.SzArray, new[] { Encode(array.Elem) }),
        TypeNode.ByRef reference => Marker("byref", new[] { Encode(reference.Of) }),
        TypeNode.Ptr pointer => Marker("pointer", new[] { Encode(pointer.Of) }),
        TypeNode.Mod modifier => Marker("modifier:" + modifier.Req, new[] { Encode(modifier.M), Encode(modifier.Of) }),
        TypeNode.Fn function => Marker("function:" + function.Suspend + ":" + (function.Recv != null)
            + ":" + (function.Ctx?.Length ?? 0), new[] { Encode(function.Ret) }
                .Concat(function.Params.Select(Encode))
                .Concat(function.Recv == null ? Array.Empty<TypeNode>() : new[] { Encode(function.Recv) })
                .Concat(function.Ctx?.Select(Encode) ?? Enumerable.Empty<TypeNode>()).ToArray()),
        _ => throw new InvalidOperationException("Unknown logical signature type"),
    };

    static TypeNode Marker(string meaning, TypeNode[] arguments)
    {
        // These are local TypeDefs, not a shared ABI vocabulary. Their authoritative
        // source owner scopes the naming domain so unrelated libraries do not define
        // the same private type name. Every member of one collision family shares it.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            MarkerOwner + "|" + meaning + ":" + arguments.Length)));
        var name = "DotKt.Runtime.CompilerServices.$Signature$" + digest;
        if (!Markers.ContainsKey(name))
            Markers[name] = new BirClassDefinition {
                Kind = "class", Name = name, Vis = "private", Generated = true,
                Base = new TypeNode.Fqn("System.Object"), Mods = new BirModifiers { Sealed = true },
                TypeParams = Enumerable.Range(0, arguments.Length).Select(index =>
                    new BirTypeParameter { Name = "T" + index, SpecialConstraints = new[] { "allowsRefStruct" } }).ToArray(),
                Methods = Array.Empty<BirFunctionDeclaration>(), Fields = Array.Empty<BirFieldDeclaration>(),
            };
        return new TypeNode.Fqn(name, arguments.Length == 0 ? null : arguments);
    }

    static bool Authored(JsonObject method) => Text(method[DeclarationIdentityBinding.Key]) != null
        && !Flag(method["generated"]);
    static bool Flag(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    static void SelfTestTypedCapture()
    {
        var methodFrame = new TypeNode.Tv("method", 0);
        var ownerFrame = new TypeNode.Tv("type", 0);
        var bytes = new byte[] { 0, 255, 0xd8, 17 };
        var parameter = new BirParameter { Name = "value", Type = methodFrame, Attrs = new[] {
            new BirAttribute { Args = new IBirAttributeValue[] { new BirOpaqueBytes { Bytes = bytes } } },
        } };
        var lambdaParameter = new BirLambdaParameter { Name = "value", Type = methodFrame };
        var variables = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { ownerFrame } } };
        var inline = new BirInlineLambda { Params = new[] { lambdaParameter } };
        var suspend = new BirNewSuspendLambda { Params = new[] { lambdaParameter }, TypeParams = variables };
        var closure = new BirNewClosure { SynthClass = new BirClosureDefinition {
            Params = new[] { lambdaParameter }, TypeParams = variables } };
        var method = new BirFunctionDeclaration { Name = "keep", DeclarationId = "selected", Params = new[] { parameter },
            TypeParams = variables, Overrides = new[] { new BirOverride { DeclarationId = "first" },
                new BirOverride(), new BirOverride { DeclarationId = "second" } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = inline },
                new BirExpressionStatement { Expr = suspend }, new BirExpressionStatement { Expr = closure } } };
        var constructor = new BirConstructor { Params = new[] { parameter } };
        var type = new BirClassDefinition { Name = "TypedCaptureOwner", Kind = "class",
            Methods = new[] { method }, Ctors = new[] { constructor } };
        var source = new BirFileDeclaration { FileClass = "TypedCaptureFile", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { type } };
        var captured = Capture(new[] { source })[0];
        var fileMethod = captured.Methods.Value[0];
        var ownedType = (BirClassDefinition)captured.Types.Value[0];
        var capturedMethod = ownedType.Methods.Value[0];
        var slot = capturedMethod.Params.Value[0];
        if (slot.NominalParameterSignatureIdentity.Value is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || slot.GenericParameterSignatureBounds.Value[0] is not TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }] }
            || capturedMethod.DeclarationId.Value != "selected"
            || !capturedMethod.SignatureOverrideDeclarations.Value.SequenceEqual(new[] { "first", "second" })
            || ((TypeNode.Fqn)fileMethod.Params.Value[0].NominalParameterSignatureIdentity.Value).Name
                == ((TypeNode.Fqn)slot.NominalParameterSignatureIdentity.Value).Name
            || ownedType.Ctors.Value[0].Params.Value[0].GenericParameterSignatureBounds.IsPresent)
            throw new InvalidOperationException("Typed signature capture changed a declaration/owner/method frame or override identity");
        var capturedInline = (BirInlineLambda)((BirExpressionStatement)capturedMethod.Body.Value[0]).Expr.Value;
        var capturedSuspend = (BirNewSuspendLambda)((BirExpressionStatement)capturedMethod.Body.Value[1]).Expr.Value;
        var capturedClosure = ((BirNewClosure)((BirExpressionStatement)capturedMethod.Body.Value[2]).Expr.Value).SynthClass.Value;
        if (!capturedInline.Params.Value[0].NominalParameterSignatureIdentity.HasValue
            || capturedInline.Params.Value[0].GenericParameterSignatureBounds.IsPresent
            || !capturedSuspend.Params.Value[0].GenericParameterSignatureBounds.HasValue
            || !capturedClosure.Params.Value[0].GenericParameterSignatureBounds.HasValue
            || parameter.NominalParameterSignatureIdentity.IsPresent || lambdaParameter.NominalParameterSignatureIdentity.IsPresent
            || source.Types.Value.Length != 1 || ReferenceEquals(method.Params.Value, capturedMethod.Params.Value)
            || !((BirOpaqueBytes)slot.Attrs.Value[0].Args.Value[0]).Bytes.Value.SequenceEqual(bytes)
            || captured.Types.Value.Skip(1).OfType<BirClassDefinition>().Any(marker =>
                marker.Vis.Value != "private" || !marker.Generated.Value || !marker.Mods.Value.Sealed.Value
                || marker.TypeParams.Value.Any(variable => !variable.SpecialConstraints.Value.SequenceEqual(new[] { "allowsRefStruct" }))))
            throw new InvalidOperationException("Typed signature capture lost a lambda/constructor edge, source ownership or frozen metadata");
        Console.WriteLine("[typed signature capture] self-test OK (owner/method frames, bounds, overrides, lambda families, frozen metadata)");
    }

    public static void SelfTest()
    {
        SelfTestTypedCapture();
        // Fixtures enter the same typed capture path as the driver; only the
        // downstream signature-allocation tests still consume the DOM frontier.
        static JsonObject CaptureFixture(JsonNode source) => Capture(new[] {
            BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(source.ToJsonString(JsonOptions.Indented))),
        })[0].ToDom();
        var source = JsonNode.Parse("""
        {"fileClass":"BoundSignatureProbe","types":[],"methods":[
          {"name":"keep","declarationId":"comparable","typeParams":[{"name":"C","constraints":[
            {"t":"fqn","name":"kotlin.Comparable","args":[{"t":"tv","scope":"method","i":0}]}]}],
            "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
            "ret":{"t":"tv","scope":"method","i":0},"body":[]},
          {"name":"keep","declarationId":"sink","typeParams":[{"name":"C","constraints":[
            {"t":"fqn","name":"Probe.Sink","args":[{"t":"tv","scope":"method","i":0}]}]}],
            "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
            "ret":{"t":"tv","scope":"method","i":0},"body":[]}]}
        """);
        JsonArray ProjectMode(bool referenceBuild)
        {
            var root = source.DeepClone();
            root = CaptureFixture(root);
            ComparableRepresentationLowering.Apply(new[] { root }, referenceBuild);
            var plan = Plan(new[] { root });
            if (plan.Count != 2) throw new InvalidOperationException("Upper-bound overloads were not allocated");
            Apply(new[] { root }, plan);
            return new JsonArray(root["methods"].AsArray().Select(method => method["params"][0]["type"].DeepClone()).ToArray());
        }
        if (!JsonNode.DeepEquals(ProjectMode(true), ProjectMode(false)))
            throw new InvalidOperationException("Source-bound signature identity differs across reference/runtime modes");
        var tv = new TypeNode.Tv("method", 0);
        var referencedMarker = new TypeNode.Fqn("Probe.ReferencedModifier", new TypeNode[] { tv });
        var referencedKey = (false, referencedMarker.Name, 1);
        ReferencedModifiers.Add(referencedKey);
        try
        {
            var signature = new TypeNode.Mod(false, referencedMarker, new TypeNode.Fqn("object"));
            var expected = new TypeNode.Mod(false, referencedMarker, tv);
            if (WithIdentityModifiers(signature, tv) != expected
                || WithIdentityModifiers(signature, expected) != expected)
                throw new InvalidOperationException("Referenced signature identity lost its value ABI or duplicated a modifier");
            var qualifier = new TypeNode.Fqn("Probe.NativeQualifier");
            if (WithIdentityModifiers(new TypeNode.Mod(true, qualifier, signature),
                    new TypeNode.Mod(true, qualifier, tv)) != new TypeNode.Mod(true, qualifier, expected))
                throw new InvalidOperationException("Referenced signature identity changed an existing native qualifier");
        }
        finally { ReferencedModifiers.Remove(referencedKey); }
        var modified = new TypeNode.Mod(false, new TypeNode.Fqn("Probe.Marker", new TypeNode[] { tv }), tv);
        var ownerVariable = new TypeNode.Tv("type", 0);
        var signatureFacts = new JsonObject {
            [NominalKey] = TypeJson.Write(new TypeNode.Fqn("Probe.Marker", new TypeNode[] { ownerVariable, tv })),
            [BoundsKey] = new JsonArray(TypeJson.Write(ownerVariable), TypeJson.Write(tv)),
        };
        RewriteParameterFacts(signatureFacts, type => SupertypeGraph.SubstOwnerTvs(type,
            new TypeNode[] { new TypeNode.Fqn("object") }));
        if (TypeJson.Read(signatureFacts[NominalKey]) != new TypeNode.Fqn("Probe.Marker",
                new TypeNode[] { new TypeNode.Fqn("object"), tv })
            || TypeJson.Read(signatureFacts[BoundsKey][0]) != new TypeNode.Fqn("object")
            || TypeJson.Read(signatureFacts[BoundsKey][1]) != tv)
            throw new InvalidOperationException("Projected signature facts lost their exact independent owner/method frames");
        if (SignatureValueTypes.Of(modified) != tv
            || !MethodVariables(new TypeNode.Nullable(tv)).SequenceEqual(new[] { 0 })
            || !MethodVariables(new TypeNode.Fqn("Probe.Box", new TypeNode[] { tv })).SequenceEqual(new[] { 0 }))
            throw new InvalidOperationException("Signature identity leaked into a value slot or missed a nested variable");
        var function = new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { tv });
        var slot = new TypeNode.Fqn("System.Object");
        var boundMarker = Marker("test-bound", System.Array.Empty<TypeNode>());
        var projection = new TypeNode.Mod(false, boundMarker, new TypeNode.Mod(false, function, slot));
        var parameter = new JsonObject {
            ["type"] = TypeJson.Write(WithIdentityModifiers(projection, slot)),
            [FunctionSignatureIdentity.Key] = TypeJson.Write(function),
        };
        if (TypeJson.Read(FunctionSignatureIdentity.SignatureType(parameter))
            is not TypeNode.Mod { M: TypeNode.Fn, Of: TypeNode.Mod { M: TypeNode.Fqn, Of: TypeNode.Fqn } })
            throw new InvalidOperationException("Bound projection duplicated a function signature modifier");
        var aliases = JsonNode.Parse("""
        {"fileClass":"AliasSignatureProbe","types":[],"methods":[
          {"name":"keep","declarationId":"first","params":[{"name":"value","type":
            {"t":"fqn","name":"Probe.FirstAlias"}}],"ret":{"t":"fqn","name":"void"},"body":[]},
          {"name":"keep","declarationId":"second","params":[{"name":"value","type":
            {"t":"fqn","name":"Probe.SecondAlias"}}],"ret":{"t":"fqn","name":"void"},"body":[]}]}
        """);
        aliases = CaptureFixture(aliases);
        var sourceCarrier = aliases.DeepClone();
        RemoveCapturedFacts(sourceCarrier);
        if (sourceCarrier["methods"].AsArray().Any(method => method["params"][0][NominalKey] != null))
            throw new InvalidOperationException("Physical signature capture leaked into a source carrier");
        aliases["methods"][0]["params"][0]["type"] = TypeJson.Fqn("object");
        aliases["methods"][1]["params"][0]["type"] = TypeJson.Fqn("System.Object");
        var aliasPlan = Plan(new[] { aliases });
        if (aliasPlan.Count != 2 || aliasPlan.Values.Any(parameters => TypeJson.Read(parameters[0]) is not TypeNode.Mod))
            throw new InvalidOperationException("Nominal aliases lost their independent signature identities");
        aliases["methods"].AsArray().Add(new JsonObject {
            ["name"] = "access", ["unsafeTargetDeclarationId"] = "first",
            ["params"] = new JsonArray(new JsonObject { ["type"] = TypeJson.Fqn("AliasSignatureProbe") },
                new JsonObject { ["type"] = TypeJson.Fqn("System.Object") }),
        });
        Apply(new[] { aliases }, aliasPlan);
        if (aliases["methods"].AsArray().Take(2).Select((method, index) => SignatureValueTypes.Of(
                TypeJson.Read(method["params"][0]["type"])) != new TypeNode.Fqn(index == 0 ? "object" : "System.Object")).Any(changed => changed))
            throw new InvalidOperationException("Nominal signature identity changed a CLR value slot");
        if (DeclarationIdentityBinding.PhysicalTypeSignature(TypeJson.Read(aliases["methods"][0]["params"][0]["type"]))
            != DeclarationIdentityBinding.PhysicalTypeSignature(TypeJson.Read(aliases["methods"][2]["params"][1]["type"])))
            throw new InvalidOperationException("UnsafeAccessor lost its selected target's signature identity");
        var slots = sourceCarrier["methods"].DeepClone().AsArray();
        foreach (var method in slots.OfType<JsonObject>()) { method["virtual"] = true; method["abstract"] = true; }
        var implementations = slots.DeepClone().AsArray();
        foreach (var method in implementations.OfType<JsonObject>())
        {
            var target = Text(method[DeclarationIdentityBinding.Key]);
            method[DeclarationIdentityBinding.Key] = target + "-override";
            method["abstract"] = false;
            method["overrides"] = new JsonArray(new JsonObject {
                ["owner"] = TypeJson.Fqn("VirtualSignatureSlot"), ["member"] = "keep", ["kind"] = "method",
                [DeclarationIdentityBinding.Key] = target,
            });
        }
        var constructors = sourceCarrier["methods"].DeepClone().AsArray();
        foreach (var constructor in constructors.OfType<JsonObject>())
        {
            constructor[DeclarationIdentityBinding.Key] = Text(constructor[DeclarationIdentityBinding.Key]) + "-ctor";
            constructor.Remove("name"); constructor.Remove("ret");
        }
        var virtualRoot = new JsonObject { ["fileClass"] = "VirtualSignatureProbe", ["methods"] = new JsonArray(),
            ["types"] = new JsonArray(
                new JsonObject { ["name"] = "VirtualSignatureSlot", ["kind"] = "interface", ["methods"] = slots },
                new JsonObject { ["name"] = "VirtualSignatureBody", ["kind"] = "class",
                    ["interfaces"] = new JsonArray(TypeJson.Fqn("VirtualSignatureSlot")),
                    ["methods"] = implementations, ["ctors"] = constructors }) };
        virtualRoot = CaptureFixture(virtualRoot);
        slots = virtualRoot["types"][0]["methods"].AsArray();
        implementations = virtualRoot["types"][1]["methods"].AsArray();
        constructors = virtualRoot["types"][1]["ctors"].AsArray();
        foreach (var method in slots.Concat(implementations).Concat(constructors))
            method["params"][0]["type"] = TypeJson.Fqn("System.Object");
        var virtualPlan = Plan(new[] { virtualRoot });
        if (virtualPlan.Count != 6 || !JsonNode.DeepEquals(virtualPlan["first"], virtualPlan["first-override"])
            || JsonNode.DeepEquals(virtualPlan["first-ctor"], virtualPlan["second-ctor"]))
            throw new InvalidOperationException("Virtual/constructor overloads lost their selected signature identities");
        Apply(new[] { virtualRoot }, virtualPlan);
        TypeNode ObjectAlias(TypeNode type) => type switch
        {
            TypeNode.Mod modifier => modifier with { Of = ObjectAlias(modifier.Of) },
            TypeNode.Fqn { Name: "System.Object" } => new TypeNode.Fqn("object"),
            _ => type,
        };
        foreach (var method in implementations)
            method["params"][0]["type"] = TypeJson.Write(ObjectAlias(TypeJson.Read(method["params"][0]["type"])));
        BindSelectedOverrides(new[] { virtualRoot }, virtualPlan, null, _ => false);
        if (implementations.Count != 2
            || implementations.OfType<JsonObject>().Any(method => method["clrInterfaceImpls"]?.AsArray().Count != 1))
            throw new InvalidOperationException("A selected virtual override lost its exact MethodImpl descriptor");
        var implicitRoot = JsonNode.Parse("""
        {"fileClass":"ImplicitSignatureProbe","methods":[],"types":[
          {"name":"ImplicitSlot","kind":"interface","methods":[
            {"name":"keep","declarationId":"implicit-slot","virtual":true,"abstract":true,
             "params":[{"name":"value","type":{"t":"fqn","name":"System.Object"}}],
             "ret":{"t":"fqn","name":"System.String"}}]},
          {"name":"ImplicitBody","kind":"class","interfaces":[{"t":"fqn","name":"ImplicitSlot"}],"methods":[
            {"name":"keep","declarationId":"implicit-left","virtual":true,
             "params":[{"name":"value","type":{"t":"fqn","name":"LogicalLeft"}}],
             "ret":{"t":"fqn","name":"System.String"}},
            {"name":"keep","declarationId":"implicit-right","virtual":true,
             "params":[{"name":"value","type":{"t":"fqn","name":"LogicalRight"}}],
             "ret":{"t":"fqn","name":"System.String"}}]}]}
        """).AsObject();
        implicitRoot = CaptureFixture(implicitRoot);
        var implicitSlot = implicitRoot["types"][0]["methods"][0].AsObject();
        var implicitBodies = implicitRoot["types"][1]["methods"].AsArray();
        RecordSelectedImplementationSlot(implicitBodies[0].AsObject(), implicitSlot);
        foreach (var method in implicitBodies.OfType<JsonObject>())
            method["params"][0]["type"] = TypeJson.Fqn("System.Object");
        var implicitPlan = Plan(new[] { implicitRoot });
        if (implicitPlan.Count != 2 || implicitPlan.ContainsKey("implicit-slot"))
            throw new InvalidOperationException("An implicit slot test did not isolate a body-only signature allocation");
        Apply(new[] { implicitRoot }, implicitPlan);
        BindSelectedOverrides(new[] { implicitRoot }, implicitPlan, null, _ => false);
        if (implicitBodies.Count != 3
            || !JsonNode.DeepEquals(implicitBodies[2]["params"][0]["type"], implicitSlot["params"][0]["type"])
            || implicitBodies[2]["clrInterfaceImpls"]?.AsArray().Count != 1
            || implicitBodies[2]["clrInterfaceImpls"][0]["member"]?.GetValue<string>() != "keep")
            throw new InvalidOperationException("A late signature allocation lost its previously selected implicit slot");
        foreach (var nativeInterface in new[] { false, true })
            RecordSelectedPhysicalSlot(implicitBodies[0].AsObject(), new JsonObject {
                ["owner"] = TypeJson.Fqn(nativeInterface ? "NativeSlot" : "NativeBase"),
                ["member"] = "keep", ["arity"] = 0,
                ["params"] = new JsonArray(TypeJson.Fqn("System.Object")),
                ["ret"] = TypeJson.Fqn("System.String"),
            }, nativeInterface);
        BindSelectedOverrides(new[] { implicitRoot }, implicitPlan, null, _ => false);
        if (implicitBodies.Count != 5 || implicitBodies[0]["clrInterfaceImpls"]?.AsArray().Count != 0
            || implicitBodies[0]["clrBaseImpls"]?.AsArray().Count != 0
            || !JsonNode.DeepEquals(implicitBodies[3]["params"][0]["type"], TypeJson.Fqn("System.Object"))
            || !JsonNode.DeepEquals(implicitBodies[4]["params"][0]["type"], TypeJson.Fqn("System.Object")))
            throw new InvalidOperationException("A late source allocation lost its selected exact native base/interface slots");
        Console.Error.WriteLine("[declaration signatures] self-test OK (source bounds, nominal aliases, twin modes, value/signature separation, source carriers)");
    }
}
