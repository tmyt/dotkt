using System.Text.Json.Nodes;
using DotKt.Bir;

// A shared-generic CLR delegate entry can fail to load generic-dependent
// optional modifiers even though an ordinary call to the same method is valid.
// Keep the selected declaration intact; give the delegate a distinct, unmodified
// physical entry which forwards directly to that declaration.
static class DelegateEntryLowering
{
    internal static void SelfTest()
    {
        foreach (var count in new[] { 1, 17 })
        foreach (var bound in new[] { false, true })
        foreach (var captured in new[] { 0, 1, 2 })
        {
            var modified = new TypeNode.Mod(false, new TypeNode.Tv("method", 0), new TypeNode.Fqn("object"));
            var signature = new JsonArray(Enumerable.Range(0, count).Select(_ => TypeJson.Write(modified)).ToArray());
            var parameters = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject {
                ["name"] = "p" + i, ["type"] = TypeJson.Write(modified) }).ToArray());
            var generic = JsonNode.Parse("""[{"name":"T","constraints":[{"t":"fqn","name":"System.IDisposable"}]}]""");
            var target = new JsonObject { ["name"] = "Selected", ["static"] = !bound,
                ["typeParams"] = generic.DeepClone(), ["params"] = parameters,
                ["ret"] = TypeJson.Fqn("object"), ["body"] = new JsonArray() };
            JsonObject Site() => new() { ["k"] = bound ? "newBoundDelegate" : "newDelegate",
                ["virtual"] = bound, ["calleeOwner"] = TypeJson.Fqn("Host"),
                ["method"] = "Selected", ["typeArgs"] = new JsonArray(TypeJson.Fqn("System.IO.MemoryStream")),
                ["calleeParams"] = signature.DeepClone() };
            var first = Site(); var second = Site();
            var methods = new JsonArray(target, new JsonObject { ["name"] = "Use", ["body"] = new JsonArray(first, second) });
            var root = new JsonObject { ["fileClass"] = "Host", ["methods"] = methods,
                ["typeParams"] = new JsonArray("Own"),
                ["capturedTypeParams"] = new JsonArray(Enumerable.Range(0, captured)
                    .Select(i => (JsonNode)JsonValue.Create("Outer" + i)).ToArray()) };
            var sameOwnerFile = new JsonObject { ["fileClass"] = "Host", ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "dotkt$delegateEntry$0", ["params"] = new JsonArray() }) };
            for (var iteration = 0; iteration < 2; iteration++)
            {
                Apply(new[] { root, sameOwnerFile });
                if (methods.Count != 3 || Text(first["method"]) != Text(second["method"])
                    || Text(first["method"]) != "dotkt$delegateEntry$1"
                    || !JsonNode.DeepEquals(generic, methods[2]["typeParams"])
                    || !JsonNode.DeepEquals(signature, methods[2]["body"][0]["value"]["calleeParams"])
                    || Text(methods[2]["body"][0]["value"]["k"]) != (bound ? "callInstance" : "callStatic")
                    || TypeJson.Read(methods[2]["body"][0]["value"]["calleeOwner"]) is not TypeNode.Fqn entryOwner
                    || entryOwner.Args?.Length != captured + 1
                    || !entryOwner.Args.SequenceEqual(Enumerable.Range(0, captured + 1)
                        .Select(i => (TypeNode)new TypeNode.Tv("type", i)))
                    || methods[2]["static"].GetValue<bool>() == bound
                    || Text(methods[2]["vis"]) != "internal" || !methods[2]["generated"].GetValue<bool>()
                    || bound && (first["virtual"].GetValue<bool>()
                        || !methods[2]["body"][0]["value"]["virtual"].GetValue<bool>()
                        || Text(methods[2]["body"][0]["value"]["recv"]["k"]) != "this")
                    || TypeJson.Read(first["calleeParams"][0]) != new TypeNode.Fqn("object")
                    || TypeJson.Read(target["params"][0]["type"]) != modified)
                    throw new InvalidOperationException("Delegate entry lost declaration identity, constraints, reuse or idempotence");
            }
        }
        Console.WriteLine("[delegate entry] self-test OK (declaration preserved, constraints, reuse, idempotence)");
    }

    public static void Apply(IEnumerable<JsonNode> roots)
    {
        var documents = roots.OfType<JsonObject>().ToArray();
        var owners = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void Register(JsonObject owner, string name)
        {
            if (name != null)
            {
                if (!owners.TryGetValue(name, out var fragments)) owners[name] = fragments = new List<JsonObject>();
                fragments.Add(owner);
            }
            if (owner["types"] is JsonArray types)
                foreach (var nested in types.OfType<JsonObject>()) Register(nested, Text(nested["name"]));
        }
        foreach (var root in documents) Register(root, Text(root["fileClass"]));
        var sites = new List<JsonObject>();
        void Collect(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Text(obj["k"]) is "newDelegate" or "newBoundDelegate" && obj["memberRef"] == null) sites.Add(obj);
                foreach (var child in obj) Collect(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Collect(child);
        }
        foreach (var root in documents) Collect(root);
        var entries = new Dictionary<(JsonObject Target, bool Virtual), JsonObject>();
        foreach (var site in sites)
        {
            if (TypeJson.Read(site["calleeOwner"]) is not TypeNode.Fqn owner
                || !owners.TryGetValue(owner.Name, out var declarations)
                || site["calleeParams"] is not JsonArray selected) continue;
            var signature = selected.Select(TypeJson.Read).ToArray();
            if (!signature.Any(HasDependentModifier)) continue;
            var arity = (site["typeArgs"] as JsonArray)?.Count ?? 0;
            var allMethods = declarations.Where(d => d["methods"] is JsonArray)
                .SelectMany(d => d["methods"].AsArray().OfType<JsonObject>()
                    .Select(method => (Declaration: d, Method: method))).ToArray();
            var candidates = allMethods.Where(candidate => Text(candidate.Method["name"]) == Text(site["method"])
                && ((candidate.Method["typeParams"] as JsonArray)?.Count ?? 0) == arity
                && candidate.Method["params"] is JsonArray ps && ps.Count == signature.Length
                && ps.Select(p => TypeJson.Read(p["type"])).SequenceEqual(signature)).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"bir2cir: delegate entry has no unique selected local declaration: {owner.Name}.{Text(site["method"])}`{arity} {selected.ToJsonString()}");
            var target = candidates[0].Method;
            var declaration = candidates[0].Declaration;
            var methods = declaration["methods"].AsArray();
            var bound = Text(site["k"]) == "newBoundDelegate";
            var virtualCall = bound && site["virtual"] is JsonValue virtualValue && virtualValue.TryGetValue<bool>(out var dispatch) && dispatch;
            if (!entries.TryGetValue((target, virtualCall), out var entry))
            {
                var ordinal = 0;
                string name;
                do name = "dotkt$delegateEntry$" + ordinal++;
                while (allMethods.Any(m => Text(m.Method["name"]) == name));
                var parameters = new JsonArray();
                var arguments = new JsonArray();
                for (var i = 0; i < signature.Length; i++)
                {
                    var parameterName = "p" + i;
                    parameters.Add(new JsonObject { ["name"] = parameterName, ["type"] = TypeJson.Write(WithoutOptionalModifiers(signature[i])) });
                    arguments.Add(new JsonObject { ["k"] = "local", ["name"] = parameterName });
                }
                var ownerArity = TypeParameterFrame.Count(declaration);
                var entryOwner = new TypeNode.Fqn(owner.Name, ownerArity == 0 ? null : Enumerable.Range(0, ownerArity)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
                var call = new JsonObject { ["k"] = bound ? "callInstance" : "callStatic", ["owner"] = TypeJson.Write(entryOwner),
                    ["calleeOwner"] = TypeJson.Write(entryOwner),
                    ["method"] = target["name"].DeepClone(), ["sig"] = selected.DeepClone(),
                    ["calleeParams"] = selected.DeepClone(), ["ret"] = target["ret"].DeepClone(),
                    ["calleeRet"] = target["ret"].DeepClone(), ["args"] = arguments,
                    ["typeArgs"] = new JsonArray(Enumerable.Range(0, arity).Select(i => TypeJson.Write(new TypeNode.Tv("method", i))).ToArray()) };
                if (bound)
                {
                    call["ownerType"] = TypeJson.Write(entryOwner);
                    call["recv"] = new JsonObject { ["k"] = "this" };
                    call["virtual"] = virtualCall;
                }
                var returnsVoid = TypeJson.Read(target["ret"]) is TypeNode.Fqn { Name: "void" or "System.Void" };
                var body = returnsVoid
                    ? new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = call }, new JsonObject { ["k"] = "return" })
                    : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call });
                entry = new JsonObject { ["name"] = name, ["static"] = !bound, ["vis"] = "internal", ["generated"] = true,
                    ["override"] = false, ["virtual"] = false, ["abstract"] = false,
                    ["typeParams"] = target["typeParams"]?.DeepClone() ?? new JsonArray(), ["params"] = parameters,
                    ["ret"] = target["ret"].DeepClone(), ["body"] = body };
                methods.Add(entry);
                entries[(target, virtualCall)] = entry;
            }
            site["method"] = entry["name"].DeepClone();
            if (bound) site["virtual"] = false;
            site["calleeParams"] = new JsonArray(entry["params"].AsArray().Select(p => p["type"].DeepClone()).ToArray());
        }
    }

    static bool HasDependentModifier(TypeNode type) => type is TypeNode.Mod modifier
        && ((!modifier.Req && ContainsVariable(TypeJson.Write(modifier.M))) || HasDependentModifier(modifier.Of));

    static bool ContainsVariable(JsonNode node) => node switch
    {
        JsonObject obj => Text(obj["t"]) == "tv" && Text(obj["scope"]) == "method"
            || obj.Any(pair => ContainsVariable(pair.Value)),
        JsonArray array => array.Any(ContainsVariable),
        _ => false,
    };

    static TypeNode WithoutOptionalModifiers(TypeNode type) => type is TypeNode.Mod modifier
        ? modifier.Req ? new TypeNode.Mod(true, modifier.M, WithoutOptionalModifiers(modifier.Of))
            : WithoutOptionalModifiers(modifier.Of)
        : type;

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
