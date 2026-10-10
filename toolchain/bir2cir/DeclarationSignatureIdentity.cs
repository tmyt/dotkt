using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Independent Kotlin overloads can have identical CLR value signatures while
// their nominal parameter types or generic parameter bounds remain distinct.
// Keep the value/generic frame unchanged; state that distinction in modopt.
static class DeclarationSignatureIdentity
{
    const string BoundsKey = "genericParameterSignatureBounds";
    const string NominalKey = "nominalParameterSignatureIdentity";
    static readonly Dictionary<string, JsonObject> Markers = new(StringComparer.Ordinal);
    static string MarkerOwner;

    public static void Capture(IEnumerable<JsonNode> roots)
    {
        Markers.Clear();
        var rootList = roots.ToArray();
        void Walk(JsonNode node, string owner)
        {
            if (node is JsonObject obj)
            {
                if (obj["methods"] is JsonArray && Text(obj["name"]) is string declarationOwner)
                    owner = declarationOwner;
                if (obj["params"] is JsonArray parameters)
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
            foreach (var type in (owner["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Collect(type, Text(type["name"]));
        }
        foreach (var root in roots.OfType<JsonObject>()) Collect(root, Text(root["fileClass"]));
        var sourceMethods = methods.Where(item => Text(item.Method[DeclarationIdentityBinding.Key]) != null)
            .GroupBy(item => Text(item.Method[DeclarationIdentityBinding.Key]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Method, StringComparer.Ordinal);
        bool Eligible(JsonObject method)
        {
            if (Independent(method)) return true;
            var id = Text(method[DeclarationIdentityBinding.Key]);
            return id?.EndsWith("|cold", StringComparison.Ordinal) == true
                && sourceMethods.TryGetValue(id[..^5], out var source) && Independent(source);
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
        return result;
    }

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

    static bool Independent(JsonObject method) => Text(method[DeclarationIdentityBinding.Key]) != null
        && !Flag(method["generated"]) && !Flag(method["virtual"]) && !Flag(method["override"])
        && !Flag(method["abstract"]) && (method["overrides"] as JsonArray)?.Count is not > 0;
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
        Console.Error.WriteLine("[declaration signatures] self-test OK (source bounds, nominal aliases, twin modes, value/signature separation, source carriers)");
    }
}
