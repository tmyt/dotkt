using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
    static readonly Dictionary<string, JsonObject> Markers = new(StringComparer.Ordinal);
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

    public static void Capture(IEnumerable<JsonNode> roots)
    {
        Markers.Clear();
        PhysicalSlots.Clear();
        var rootList = roots.ToArray();
        void Walk(JsonNode node, string owner)
        {
            if (node is JsonObject obj)
            {
                if (obj["methods"] is JsonArray && Text(obj["name"]) is string declarationOwner)
                    owner = declarationOwner;
                if (obj["params"] is JsonArray parameters)
                {
                    if (obj["overrides"] is JsonArray overrides)
                        obj[OverrideEdgesKey] = new JsonArray(overrides.OfType<JsonObject>()
                            .Select(edge => Text(edge[DeclarationIdentityBinding.Key])).Where(id => id != null)
                            .Select(id => (JsonNode)JsonValue.Create(id)).ToArray());
                    foreach (var parameter in parameters.OfType<JsonObject>())
                    {
                        if (TypeJson.Read(parameter["type"]) is not TypeNode logicalType) continue;
                        MarkerOwner = owner;
                        parameter[NominalKey] = TypeJson.Write(Encode(logicalType));
                        var variables = obj["typeParams"] as JsonArray;
                        var bounds = MethodVariables(logicalType)
                            .Distinct().OrderBy(index => index)
                            .Where(index => index >= 0 && variables != null && index < variables.Count)
                            .SelectMany(index => variables[index] is JsonObject declaration
                                && declaration["constraints"] is JsonArray constraints
                                ? constraints.Select(TypeJson.Read).Select(Encode)
                                : Enumerable.Empty<TypeNode>()).ToArray();
                        if (bounds.Length > 0)
                            parameter[BoundsKey] = new JsonArray(bounds.Select(TypeJson.Write).ToArray());
                    }
                }
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) Walk(child, owner);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Walk(child, owner);
        }
        foreach (var root in rootList) Walk(root, Text((root as JsonObject)?["fileClass"]));
        if (rootList.FirstOrDefault() is JsonObject first)
        {
            if (first["types"] is not JsonArray types) first["types"] = types = new JsonArray();
            foreach (var marker in Markers.Values) types.Add(marker.DeepClone());
        }
    }

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

    public static IReadOnlyDictionary<string, JsonArray> Plan(IEnumerable<JsonNode> roots)
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
        foreach (var group in methods.GroupBy(item =>
                     (item.Owner, item.Name, Signature: DeclarationIdentityBinding.PhysicalSignature(item.Method))))
        {
            var candidates = group.ToArray();
            if (candidates.Length < 2 || candidates.Any(item => !Eligible(item.Method))) continue;
            var projected = candidates.Select(item => (item.Method, Parameters: Project(item.Method))).ToArray();
            if (projected.Select(item => item.Parameters.ToJsonString()).Distinct(StringComparer.Ordinal).Count()
                != candidates.Length)
                projected = candidates.Select(item => (item.Method, Parameters: Project(item.Method, nominal: true))).ToArray();
            if (projected.Select(item => item.Parameters.ToJsonString()).Distinct(StringComparer.Ordinal).Count()
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
                if (!result.TryGetValue(target, out var signature)) continue;
                var selectedOwner = SelectedLocalOwner(definitions[methodOwners[id]], methodOwners[target], definitions, null);
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
            if (!JsonNode.DeepEquals(targetParameters, bodyParameters))
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
        var underlying = WithIdentityModifiers(modifier.Of, valueType);
        return modifier.M is TypeNode.Fqn marker && Markers.ContainsKey(marker.Name)
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
            Markers[name] = new JsonObject {
                ["kind"] = "class", ["name"] = name, ["vis"] = "private", ["generated"] = true,
                ["base"] = TypeJson.Fqn("System.Object"), ["mods"] = new JsonObject { ["sealed"] = true },
                ["typeParams"] = new JsonArray(Enumerable.Range(0, arguments.Length).Select(index =>
                    (JsonNode)new JsonObject { ["name"] = "T" + index,
                        ["specialConstraints"] = new JsonArray("allowsRefStruct") }).ToArray()),
                ["methods"] = new JsonArray(), ["fields"] = new JsonArray(),
            };
        return new TypeNode.Fqn(name, arguments.Length == 0 ? null : arguments);
    }

    static bool Authored(JsonObject method) => Text(method[DeclarationIdentityBinding.Key]) != null
        && !Flag(method["generated"]);
    static bool Flag(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    public static void SelfTest()
    {
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
            Capture(new[] { root });
            ComparableRepresentationLowering.Apply(new[] { root }, referenceBuild);
            var plan = Plan(new[] { root });
            if (plan.Count != 2) throw new InvalidOperationException("Upper-bound overloads were not allocated");
            Apply(new[] { root }, plan);
            return new JsonArray(root["methods"].AsArray().Select(method => method["params"][0]["type"].DeepClone()).ToArray());
        }
        if (!JsonNode.DeepEquals(ProjectMode(true), ProjectMode(false)))
            throw new InvalidOperationException("Source-bound signature identity differs across reference/runtime modes");
        var tv = new TypeNode.Tv("method", 0);
        var modified = new TypeNode.Mod(false, new TypeNode.Fqn("Probe.Marker", new TypeNode[] { tv }), tv);
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
        Capture(new[] { aliases });
        var sourceCarrier = aliases.DeepClone();
        RemoveCapturedFacts(sourceCarrier);
        if (sourceCarrier["methods"].AsArray().Any(method => method["params"][0][NominalKey] != null))
            throw new InvalidOperationException("Physical signature capture leaked into a source carrier");
        foreach (var method in aliases["methods"].AsArray())
            method["params"][0]["type"] = TypeJson.Fqn("System.Object");
        var aliasPlan = Plan(new[] { aliases });
        if (aliasPlan.Count != 2)
            throw new InvalidOperationException("Nominal aliases lost their independent signature identities");
        Apply(new[] { aliases }, aliasPlan);
        if (aliases["methods"].AsArray().Any(method => SignatureValueTypes.Of(
                TypeJson.Read(method["params"][0]["type"])) is not TypeNode.Fqn { Name: "System.Object" }))
            throw new InvalidOperationException("Nominal signature identity changed a CLR value slot");
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
        Capture(new[] { virtualRoot });
        foreach (var method in slots.Concat(implementations).Concat(constructors))
            method["params"][0]["type"] = TypeJson.Fqn("System.Object");
        var virtualPlan = Plan(new[] { virtualRoot });
        if (virtualPlan.Count != 6 || !JsonNode.DeepEquals(virtualPlan["first"], virtualPlan["first-override"])
            || JsonNode.DeepEquals(virtualPlan["first-ctor"], virtualPlan["second-ctor"]))
            throw new InvalidOperationException("Virtual/constructor overloads lost their selected signature identities");
        Apply(new[] { virtualRoot }, virtualPlan);
        BindSelectedOverrides(new[] { virtualRoot }, virtualPlan, null, _ => false);
        if (implementations.OfType<JsonObject>().Any(method => method["clrInterfaceImpls"]?.AsArray().Count != 1))
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
        Capture(new[] { implicitRoot });
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
