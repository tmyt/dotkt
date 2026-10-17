using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

sealed record ValueRepresentationBinding(string Storage, string Classifier, string ClassifierFactory);

// Declaration-owned value storage is independent of the runtime inheritance classifier.
// The trusted stdlib supplies all type/member/classifier choices. No source name selects a rule.
sealed class ValueRepresentationLowering
{
    internal const string TypeAttribute = "kotlin.clr.ClrValueRepresentation";
    internal const string MemberAttribute = "kotlin.clr.ClrValueMember";
    readonly Dictionary<string, ValueRepresentationBinding> bindings = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> adapters = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, JsonObject Method)> locals = new(StringComparer.Ordinal);
    readonly ReferenceMetadataIndex refs;
    readonly bool referenceBuild;

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
    static JsonObject Attribute(JsonObject declaration, string name) =>
        (declaration["attrs"] as JsonArray)?.OfType<JsonObject>()
            .SingleOrDefault(attribute => TypeJson.OwnerName(attribute["attr"]) == name);
    static string Argument(JsonObject attribute, int index) => Text(attribute?["args"]?[index]?["value"]);

    internal ValueRepresentationLowering(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex references, bool refBuild)
    {
        refs = references;
        referenceBuild = refBuild;
        foreach (var assembly in refs?.Assemblies ?? Array.Empty<ReferenceAssembly>())
            foreach (var (name, binding) in assembly.DotKt.ValueRepresentations) bindings[name] = binding;
        void Index(JsonObject declaration)
        {
            if (Text(declaration["name"]) is string name && Attribute(declaration, TypeAttribute) is { } attribute)
                bindings[name] = new(Argument(attribute, 0), Argument(attribute, 1), Argument(attribute, 2));
            var owner = Text(declaration["fileClass"]) ?? Text(declaration["name"]);
            foreach (var method in (declaration["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(method[DeclarationIdentityBinding.Key]) is string id)
                {
                    locals[id] = (owner, method);
                    if (Attribute(method, MemberAttribute) is { } marker) adapters[id] = Argument(marker, 0);
                }
            foreach (var type in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Index(type);
        }
        foreach (var root in roots.OfType<JsonObject>()) Index(root);
    }

    internal JsonNode Predicate(TypeNode type)
    {
        var (binding, depth) = Classifier(type);
        return !referenceBuild && binding != null
            ? Helper(binding.ClassifierFactory, new[] { Depth(depth) }, new[] { new TypeNode.Fqn("kotlin.Int") },
                KotlinRuntimeTypeWitness.PredicateType) : null;
    }

    (ValueRepresentationBinding Binding, int Depth) Classifier(TypeNode type)
    {
        var depth = 0;
        for (;;)
            switch (type)
            {
                case TypeNode.Nullable nullable: type = nullable.Of; break;
                case TypeNode.Oblivious oblivious: type = oblivious.Of; break;
                case TypeNode.Projection { Variance: "out" } projection: type = projection.Of; break;
                case TypeNode.Array { SzArray: true, Rank: 1 } array: type = array.Elem; depth++; break;
                case TypeNode.Fqn f when bindings.TryGetValue(f.Name, out var binding): return (binding, depth);
                default: return (null, 0);
            }
    }

    static JsonObject Depth(int depth) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = depth };

    JsonObject Helper(string target, IReadOnlyList<JsonNode> arguments, IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var split = target.LastIndexOf('.');
        var owner = target[..split];
        var name = target[(split + 1)..];
        var local = locals.Where(entry => entry.Value.Owner == owner && Text(entry.Value.Method["name"]) == name)
            .Where(entry => entry.Value.Method["params"] is JsonArray declared
                && declared.Count == parameters.Count
                && declared.OfType<JsonObject>().Select((parameter, index) =>
                    ReferenceMetadataIndex.SourceDeclarationDescribesCall(TypeJson.Read(parameter["type"]), parameters[index])).All(match => match))
            .Select(entry => entry.Key).ToArray();
        var identity = local.Length == 1 ? local[0]
            : refs.AuthoredKotlinHelper(owner, name, 0, parameters.ToArray()).DeclarationId;
        return new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(owner), ["method"] = name,
            [DeclarationIdentityBinding.Key] = identity,
            ["sig"] = new JsonArray(parameters.Select(TypeJson.Write).ToArray()),
            ["args"] = new JsonArray(arguments.Select(argument => argument.DeepClone()).ToArray()),
            ["ret"] = TypeJson.Write(result), ["sty"] = TypeJson.Write(result),
        };
    }

    // Freeze source constraints before companion frames are materialized. A CLR bound naming
    // the runtime class would reject native values admitted by the Kotlin declaration.
    internal void EraseBounds(JsonNode root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        void Walk(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array) Walk(child); return; }
            if (node is not JsonObject declaration) return;
            if (declaration["k"] == null && declaration["typeParams"] is JsonArray parameters)
            {
                var bounds = new JsonObject();
                var changed = false;
                for (var index = 0; index < parameters.Count; index++)
                    if (parameters[index] is JsonObject parameter && parameter["constraints"] is JsonArray constraints)
                    {
                        bounds[index.ToString()] = constraints.DeepClone();
                        changed |= constraints.Select(TypeJson.Read).Any(type => type != null && !type.Equals(Storage(type)));
                        var retained = constraints.Where(bound => TypeJson.Read(bound) is not TypeNode.Fqn f
                            || !bindings.ContainsKey(f.Name)).Select(bound => bound.DeepClone()).ToArray();
                        if (retained.Length == constraints.Count) continue;
                        parameter["constraints"] = new JsonArray(retained);
                        changed = true;
                    }
                if (changed)
                {
                    if (declaration["kind"] != null)
                        KotlinSupertypesRecord.Merge(declaration, new JsonObject { ["bounds"] = bounds });
                    else
                    {
                        var payload = Text(declaration[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string encoded
                            ? JsonNode.Parse(encoded).AsObject() : new JsonObject();
                        var saved = payload["bounds"] as JsonObject ?? new JsonObject();
                        if (payload["bounds"] == null) payload["bounds"] = saved;
                        foreach (var (index, value) in bounds)
                            if (!saved.ContainsKey(index)) saved[index] = value.DeepClone();
                        declaration[NullableGenericErasure.MethodTypeParameterBoundsPre] = payload.ToJsonString();
                    }
                }
            }
            foreach (var (key, child) in declaration.ToArray())
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Walk(child);
        }
        Walk(root);
    }

    internal void BindOperations(JsonNode root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        var suffix = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Names(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                if (Text(obj["name"]) is string name) names.Add(name);
                if (Text(obj["var"]) is string variable) names.Add(variable);
                foreach (var child in obj) Names(child.Value);
            }
            else if (current is JsonArray array) foreach (var child in array) Names(child);
        }
        Names(root);
        void Walk(JsonNode node)
        {
            if (node is JsonArray array) { foreach (var child in array.ToArray()) Walk(child); return; }
            if (node is not JsonObject operation) return;
            foreach (var (key, child) in operation.ToArray())
                if (key is not ("attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey)) Walk(child);
            var kind = Text(operation["k"]);
            if (kind == "callInstance" && operation["super"]?.GetValue<bool>() != true
                && Text(operation[DeclarationIdentityBinding.Key]) is string id
                && (adapters.GetValueOrDefault(id) ?? refs?.ValueAdapter(id)) is string target)
            {
                var arguments = new[] { operation["recv"] }.Concat(operation["args"] as JsonArray ?? new JsonArray()).ToArray();
                var signature = new[] { new TypeNode.Fqn("kotlin.Any") }.Concat(
                    (operation["sig"] as JsonArray ?? new JsonArray()).Select(TypeJson.Read)).ToArray();
                var memberResult = TypeJson.Read(operation["dynRet"] ?? operation["ret"] ?? operation["sty"])
                    ?? throw new InvalidOperationException("Value member adapter has no selected return type");
                Replace(operation, Helper(target, arguments, signature, memberResult));
                return;
            }
            if (kind is not ("cast" or "isInst" or "isInstRef") || operation["e"] == null) return;
            var source = TypeJson.Read(operation["type"]);
            var nullable = source is TypeNode.Nullable;
            var (binding, depth) = Classifier(source);
            if (binding == null) return;
            string temp;
            do temp = "$valueClassifier$" + suffix++; while (!names.Add(temp));
            var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            JsonObject Local() => new() { ["k"] = "local", ["name"] = temp, ["sty"] = TypeJson.Write(any) };
            JsonObject Null() => new() { ["k"] = "const", ["type"] = TypeJson.Write(any), ["value"] = null };
            JsonObject Bool(bool value) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = value };
            var matches = new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = Local(), ["rhs"] = Null() },
                ["then"] = Bool(nullable),
                ["else"] = new JsonObject {
                    ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Boolean"),
                    ["cond"] = Helper(binding.Classifier, new[] { Local(), Depth(depth) },
                        new TypeNode[] { any, new TypeNode.Fqn("kotlin.Int") }, new TypeNode.Fqn("kotlin.Boolean")),
                    ["then"] = new JsonObject { ["k"] = "isInst", ["type"] = TypeJson.Write(source), ["e"] = Local() },
                    ["else"] = Bool(false),
                },
            };
            var statements = new JsonArray(new JsonObject { ["k"] = "var", ["name"] = temp,
                ["type"] = TypeJson.Write(any), ["init"] = operation["e"].DeepClone() });
            JsonNode result;
            if (kind == "cast")
            {
                statements.Add(new JsonObject { ["k"] = "if", ["branches"] = new JsonArray(new JsonObject {
                    ["cond"] = new JsonObject { ["k"] = "unaryOp", ["op"] = "!", ["e"] = matches },
                    ["body"] = new JsonArray(new JsonObject { ["k"] = "throw", ["value"] = new JsonObject {
                        ["k"] = "new", ["type"] = TypeJson.Fqn("kotlin.ClassCastException"),
                        ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                    } }),
                }) });
                result = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(source), ["e"] = Local() };
            }
            else if (kind == "isInst") result = matches;
            else result = new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Write(source is TypeNode.Nullable ? source : new TypeNode.Nullable(source)),
                ["cond"] = matches, ["then"] = new JsonObject { ["k"] = kind, ["type"] = TypeJson.Write(source), ["e"] = Local() },
                ["else"] = Null(),
            };
            Replace(operation, new JsonObject { ["k"] = "valueBlock", ["stmts"] = statements, ["result"] = result });
        }
        Walk(root);
    }

    internal TypeNode Storage(TypeNode type) => type switch {
        TypeNode.Fqn f when bindings.TryGetValue(f.Name, out var binding) => new TypeNode.Fqn(binding.Storage),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(Storage).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Storage(a.Elem), a.Rank, a.SzArray),
        TypeNode.Nullable n => new TypeNode.Nullable(Storage(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Storage(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(Storage(b.Of)),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, Storage(p.Of)),
        TypeNode.Fn f => f with { Ret = Storage(f.Ret), Params = f.Params.Select(Storage).ToArray(),
            Recv = f.Recv == null ? null : Storage(f.Recv), Ctx = f.Ctx?.Select(Storage).ToArray() },
        _ => type,
    };

    internal void ProjectValues(JsonNode root, bool preserveOnly = false)
    {
        if (referenceBuild || bindings.Count == 0) return;
        void Slot(JsonObject declaration, string key, string carrier)
        {
            if (TypeJson.Read(declaration[key]) is not TypeNode original || original.Equals(Storage(original))) return;
            declaration[carrier] ??= TypeNode.ToJson(original);
        }
        void Walk(JsonNode node)
        {
            if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    if (!preserveOnly && TypeJson.Read(array[index]) is TypeNode type) array[index] = TypeJson.Write(Storage(type));
                    else Walk(array[index]);
                return;
            }
            if (node is not JsonObject obj) return;
            if (obj["k"] == null)
            {
                Slot(obj, "ret", "retKotlinType");
                Slot(obj, "type", "kotlinType");
                if (obj["kind"] != null)
                {
                    var pre = new JsonObject();
                    if (obj["interfaces"] is JsonArray interfaces && interfaces.Select(TypeJson.Read)
                        .Any(type => type != null && !type.Equals(Storage(type)))) pre["interfaces"] = interfaces.DeepClone();
                    if (TypeJson.Read(obj["base"]) is TypeNode parent && !parent.Equals(Storage(parent)))
                        pre["base"] = obj["base"].DeepClone();
                    KotlinSupertypesRecord.Merge(obj, pre);
                }
            }
            foreach (var (key, child) in obj.ToArray())
            {
                if (key is "attrs" or "overrides" or "memberRef" or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                // These are selected declaration descriptors, not argument value slots. An ordinary CLR
                // declaration keeps its native parameter types even when Kotlin values use a broader storage ABI.
                var declarationOwner = TypeJson.Read(obj["ownerType"] ?? obj["owner"]
                    ?? (Text(obj["k"]) is "new" or "newClr" or "clrInstance" or "clrStatic" ? obj["type"] : null));
                if (key is "sig" or "argTypes" or "memberSignature" or "resolvedMemberParams"
                    && declarationOwner is TypeNode.Fqn nativeOwner && refs != null
                    && refs.ResolveForeignProjectionType(nativeOwner.Name, nativeOwner.Args ?? Array.Empty<TypeNode>()) != null)
                    continue;
                // Physical heads are not value slots. Their supplied generic arguments still are.
                var exact = key is "base" or "owner" or "ownerType" or "declaringType" or "iface"
                    || key == "type" && Text(obj["k"]) is "new" or "classRef" or "clrInstance" or "clrStatic" or "newClr";
                if (!preserveOnly && TypeJson.Read(child) is TypeNode type)
                {
                    var mapped = exact && type is TypeNode.Fqn f
                        ? new TypeNode.Fqn(f.Name, f.Args?.Select(Storage).ToArray()) : Storage(type);
                    obj[key] = TypeJson.Write(mapped);
                }
                else Walk(child);
            }
        }
        Walk(root);
    }

    static void Replace(JsonObject target, JsonObject replacement)
    {
        var position = target["pos"]?.DeepClone();
        target.Clear();
        foreach (var (key, value) in replacement.ToArray()) target[key] = value?.DeepClone();
        if (position != null) target["pos"] = position;
    }

    internal static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"fileClass":"Helpers","types":[{"name":"Union","kind":"class","attrs":[
          {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueRepresentation"},"args":[
           {"value":"System.Object"},{"value":"Helpers.matches"},{"value":"Helpers.classifier"}]}],
          "methods":[{"name":"convert","declarationId":"convert","attrs":[
           {"attr":{"t":"fqn","name":"kotlin.clr.ClrValueMember"},"args":[{"value":"Helpers.convert"}]}]}]},
          {"name":"Child","kind":"class","base":{"t":"fqn","name":"Union"}}],"methods":[
          {"name":"matches","declarationId":"matches","params":[{"type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}}},{"type":{"t":"fqn","name":"kotlin.Int"}}]},
          {"name":"classifier","declarationId":"classifier","params":[{"type":{"t":"fqn","name":"kotlin.Int"}}]},
          {"name":"convert","declarationId":"helper-convert","params":[{"type":{"t":"fqn","name":"kotlin.Any"}}]},
          {"name":"use","params":[{"name":"value","type":{"t":"fqn","name":"Union"}}],
           "ret":{"t":"array","elem":{"t":"fqn","name":"Union"}},
           "typeParams":[{"name":"T","constraints":[{"t":"fqn","name":"Union"}]}],"body":[
            {"k":"return","value":{"k":"callInstance","declarationId":"convert",
             "recv":{"k":"local","name":"value"},"args":[],"sig":[],"ret":{"t":"fqn","name":"kotlin.Int"}}},
            {"k":"exprStmt","expr":{"k":"isInst","type":{"t":"fqn","name":"Union"},"e":{"k":"local","name":"value"}}},
            {"k":"exprStmt","expr":{"k":"classRef","type":{"t":"fqn","name":"Union"}}},
            {"k":"exprStmt","expr":{"k":"callStatic","sig":[{"t":"fqn","name":"Union"}]}}]}]}
        """);
        var policy = new ValueRepresentationLowering(new[] { root }, null, false);
        var scalar = policy.Predicate(new TypeNode.Fqn("Union"));
        var vector = policy.Predicate(new TypeNode.Array(new TypeNode.Fqn("Union")));
        policy.EraseBounds(root);
        policy.BindOperations(root);
        policy.ProjectValues(root);
        var method = root["methods"][3];
        if (TypeJson.OwnerName(method["params"][0]["type"]) != "System.Object"
            || TypeJson.Read(method["ret"]) != new TypeNode.Array(new TypeNode.Fqn("System.Object"))
            || method["params"][0]["kotlinType"] == null || method["retKotlinType"] == null
            || method["typeParams"][0]["constraints"].AsArray().Count != 0
            || Text(method[NullableGenericErasure.MethodTypeParameterBoundsPre])?.Contains("Union") != true
            || Text(method["body"][0]["value"][DeclarationIdentityBinding.Key]) != "helper-convert"
            || Text(method["body"][1]["expr"]["k"]) != "valueBlock"
            || TypeJson.OwnerName(method["body"][2]["expr"]["type"]) != "Union"
            || TypeJson.OwnerName(method["body"][3]["expr"]["sig"][0]) != "System.Object"
            || TypeJson.OwnerName(root["types"][1]["base"]) != "Union"
            || scalar["args"][0]["value"].GetValue<int>() != 0
            || vector["args"][0]["value"].GetValue<int>() != 1)
            throw new InvalidOperationException("Value representation lost source types, bounds, exact heads, or classifier depth");
        Console.WriteLine("[value representation] self-test OK (metadata-driven storage, source carriers, bounds, exact heads, adapters, array classifiers)");
    }
}
