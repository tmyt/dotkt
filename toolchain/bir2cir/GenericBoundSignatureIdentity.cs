using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Independent Kotlin overloads can have identical
// CLR value signatures while their generic parameter bounds remain distinct.
// Keep the value/generic frame unchanged; state that distinction in modopt.
static class GenericBoundSignatureIdentity
{
    const string BoundsKey = "genericParameterSignatureBounds";
    static readonly Dictionary<string, JsonObject> Markers = new(StringComparer.Ordinal);

    public static void Capture(IEnumerable<JsonNode> roots)
    {
        Markers.Clear();
        var rootList = roots.ToArray();
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["params"] is JsonArray parameters && obj["typeParams"] is JsonArray variables)
                    foreach (var parameter in parameters.OfType<JsonObject>())
                    {
                        if (TypeJson.Read(parameter["type"]) is TypeNode.Tv { Scope: "method" } variable
                            && variable.I < variables.Count
                            && variables[variable.I] is JsonObject declaration
                            && declaration["constraints"] is JsonArray bounds)
                            parameter[BoundsKey] = new JsonArray(bounds.Select(TypeJson.Read)
                                .Select(Encode).Select(TypeJson.Write).ToArray());
                    }
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) Walk(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) Walk(child);
        }
        foreach (var root in rootList) Walk(root);
        if (rootList.FirstOrDefault() is JsonObject first)
        {
            if (first["types"] is not JsonArray types) first["types"] = types = new JsonArray();
            foreach (var marker in Markers.Values) types.Add(marker.DeepClone());
        }
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
                            parameters[index]["type"] = types[index].DeepClone();
                    }
                    else if (Text(obj["k"]) is "callStatic" or "callInstance" or "constrainedCall"
                        or "newDelegate" or "newBoundDelegate")
                        obj["calleeParams"] = types.DeepClone();
                }
                obj.Remove(BoundsKey);
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

    static JsonArray Project(JsonObject method)
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
            projected.Add(TypeJson.Write(type));
        }
        return projected;
    }

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
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(meaning + ":" + arguments.Length)));
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
}
