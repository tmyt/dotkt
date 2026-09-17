using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Select the binding-owned Kotlin implementation while receiver arguments still denote source types.
// The resulting ordinary declaration-bound call participates in demand analysis and frame materialization;
// selecting it after erasure would require guessing source arguments from an invariant CLR storage type.
static class CollectionHelperBinding
{
    const string HelperOwner = "kotlin.collections.ClrMapDefaultsKt";

    public static void Apply(IEnumerable<JsonNode> roots, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, string> aliases, Action<Action<JsonObject>> prepareHelpers = null,
        AliasConstructorDelegationExpansion constructors = null)
    {
        var inputs = roots.ToArray();
        var concreteIteratorTypes = inputs.SelectMany(MemberCallSubstitution.CollectConcreteIteratorTypes)
            .ToHashSet(StringComparer.Ordinal);
        var localHelpers = inputs.OfType<JsonObject>()
            .SelectMany(root => (root["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(method => (Owner: Text(root["fileClass"]), Method: method))).ToArray();
        var localKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        void CollectKinds(JsonObject owner)
        {
            if (owner["types"] is not JsonArray types) return;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Text(type["name"]) is string name) localKinds[name] = Text(type["kind"]);
                CollectKinds(type);
            }
        }
        foreach (var input in inputs.OfType<JsonObject>()) CollectKinds(input);
        bool IsInterface(string owner) => localKinds.TryGetValue(owner, out var kind) ? kind == "interface"
            : references != null && references.TryResolveClrOwner(owner, out _, out var referencedKind) && referencedKind == "interface";

        string SelectHelper(string owner, string name, int arity, JsonArray signature)
        {
            var parameters = signature.Select(TypeJson.Read).ToArray();
            var matches = localHelpers.Where(entry => entry.Owner == owner).Select(entry => entry.Method)
                .Where(method => Text(method["name"]) == name
                && method["static"]?.GetValue<bool>() == true
                && ((method["typeParams"] as JsonArray)?.Count ?? 0) == arity
                && method["params"] is JsonArray declared
                && declared.Count == parameters.Length
                && declared.OfType<JsonObject>().Select((parameter, index) =>
                    ReferenceMetadataIndex.SourceDeclarationDescribesCall(TypeJson.Read(parameter["type"]), parameters[index]))
                    .All(match => match)).ToArray();
            if (matches.Length == 1)
                return Text(matches[0][DeclarationIdentityBinding.Key])
                    ?? throw new InvalidOperationException("Collection helper has no declaration identity");
            if (matches.Length > 1)
                throw new InvalidOperationException("Ambiguous source collection helper declaration: " + owner + "." + name);
            return references?.AuthoredKotlinHelper(owner, name, arity, parameters).DeclarationId
                ?? throw new InvalidOperationException("Missing source collection helper declaration: " + owner + "." + name);
        }

        TypeNode SourceArgument(TypeNode argument) => argument switch {
            TypeNode.Projection projection => SourceArgument(projection.Of),
            TypeNode.Star => new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
            _ => argument,
        };

        void BindAuthoredCall(JsonObject call)
        {
            var arguments = call["typeArgs"] as JsonArray;
            call[DeclarationIdentityBinding.Key] = SelectHelper(TypeJson.OwnerName(call["owner"]),
                Text(call["method"]), arguments?.Count ?? 0, (JsonArray)call["sig"]);
            if (arguments != null)
                call["typeArgs"] = new JsonArray(arguments.Select(TypeJson.Read).Select(SourceArgument).Select(TypeJson.Write).ToArray());
        }

        prepareHelpers?.Invoke(BindAuthoredCall);

        JsonNode Walk(JsonNode node, MemberCallSubstitution.SubstCtx context)
        {
            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is { } item && Walk(item, context) is { } next && !ReferenceEquals(item, next)) array[i] = next;
                return array;
            }
            if (node is not JsonObject obj) return node;
            context = context.Extend(obj);
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (key is "attrs" or "overrides" or DeclarationIdentityBinding.SemanticSignatureKey) continue;
                var child = obj[key];
                if (child != null && Walk(child, context) is { } next && !ReferenceEquals(child, next)) obj[key] = next;
            }
            if (Text(obj["k"]) == "new"
                && TypeJson.Read(obj["type"]) is TypeNode.Fqn { Args.Length: 2 } constructed
                && aliases.ContainsKey(constructed.Name)
                && obj["args"] is JsonArray { Count: 1 } constructorArguments
                && obj["argTypes"] is JsonArray constructorSignature
                && (constructors != null
                    ? constructors.CollectionCopyConstructorKind(constructed.Name, constructorSignature.Select(TypeJson.Read).ToArray(), constructed.Args)
                    : references?.CollectionCopyConstructorKind(constructed.Name, constructorSignature.Select(TypeJson.Read).ToArray(), constructed.Args)) == "map")
                return MemberCallSubstitution.MapCopyConstruction(constructed, constructed.Args,
                    constructorArguments[0], BindAuthoredCall);
            if (Text(obj["k"]) != "callInstance"
                || obj["clrOwnerResolved"]?.GetValue<bool>() == true
                || TypeJson.Read(obj["ownerType"]) is not TypeNode.Fqn owner
                || obj["args"] is not JsonArray args)
                return obj;
            JsonObject call;
            if (!aliases.ContainsKey(owner.Name))
            {
                call = MemberCallSubstitution.MissingCollectionIteratorCall(obj, owner, args,
                    concreteIteratorTypes.Contains(owner.Name)
                        || references?.DeclaresConcreteIterator(owner.Name) == true) as JsonObject;
                if (call == null) return obj;
            }
            else if (owner.Name is "kotlin.collections.Map" or "kotlin.collections.MutableMap")
            {
                var helper = MemberCallSubstitution.MapDefaultHelper(Text(obj["method"]),
                    Text(obj[KotlinPropertyAccessors.KindKey]) ?? Text(obj["prop"]), args.Count,
                    owner.Name == "kotlin.collections.MutableMap");
                if (helper == null || owner.Args is not { Length: 2 }) return obj;
                var (keyType, valueType) = MemberCallSubstitution.MapKvArgs(obj, references, context, owner);
                var callArguments = new JsonArray(obj["recv"]?.DeepClone()
                    ?? throw new InvalidOperationException("Map member has no receiver"));
                foreach (var argument in args) callArguments.Add(argument?.DeepClone());
                call = new JsonObject {
                    ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(HelperOwner), ["method"] = helper,
                    ["sig"] = MemberCallSubstitution.MapHelperSig(helper),
                    ["typeArgs"] = new JsonArray(TypeJson.Write(keyType), TypeJson.Write(valueType)), ["args"] = callArguments,
                };
            }
            else
            {
                if (!IsInterface(owner.Name)
                    || !(owner.Name.StartsWith("kotlin.collections.", StringComparison.Ordinal) || owner.Name == "kotlin.sequences.Sequence"))
                    return obj;
                call = (MemberCallSubstitution.CollectionMutationCall(obj, owner, args, references, context)
                    ?? MemberCallSubstitution.CollectionDefaultCall(obj, owner, args, references, context)) as JsonObject;
                if (call == null) return obj;
            }
            var sourceArguments = ((JsonArray)call["typeArgs"]).Select(TypeJson.Read).Select(SourceArgument).ToArray();
            call["typeArgs"] = new JsonArray(sourceArguments.Select(TypeJson.Write).ToArray());
            call[DeclarationIdentityBinding.Key] = SelectHelper(TypeJson.OwnerName(call["owner"]),
                Text(call["method"]), sourceArguments.Length, (JsonArray)call["sig"]);
            var result = obj["sty"] ?? obj["ret"];
            call["ret"] = result?.DeepClone();
            call["sty"] = result?.DeepClone();
            return call;
        }
        foreach (var root in inputs) Walk(root, new MemberCallSubstitution.SubstCtx());
    }

    public static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"fileClass":"kotlin.collections.ClrMapDefaultsKt","methods":[
          {"name":"clrMapKeys","declarationId":"map-keys","static":true,"typeParams":["K","V"],
           "params":[{"name":"m","type":{"t":"fqn","name":"kotlin.Any"}}],
           "ret":{"t":"fqn","name":"kotlin.collections.Set","args":[{"t":"tv","scope":"method","i":0}]},"body":[]},
          {"name":"use","declarationId":"map-use","static":true,"typeParams":["A","B"],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callInstance","ownerType":{"t":"fqn","name":"kotlin.collections.Map","args":[
              {"t":"projection","variance":"out","of":{"t":"tv","scope":"method","i":1}},
              {"t":"tv","scope":"method","i":0}]},
             "method":"keys","prop":"get","recv":{"k":"local","name":"map"},"args":[],
             "sty":{"t":"fqn","name":"kotlin.collections.Set","args":[{"t":"tv","scope":"method","i":1}]}}]}]}
        """)!.AsObject();
        var aliases = new Dictionary<string, string> {
            ["kotlin.collections.Map"] = "System.Collections.Generic.IDictionary",
            ["kotlin.collections.Set"] = "System.Collections.Generic.ISet",
        };
        Apply(new[] { root }, null, aliases);
        var call = root["methods"][1]["body"][0];
        if (Text(call[DeclarationIdentityBinding.Key]) != "map-keys" || Text(call["k"]) != "callStatic"
            || TypeJson.Read(call["typeArgs"][0]) != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Map helper binding lost the selected source argument");
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false,
            policy: new GenericRepresentationPolicy(aliases));
        if (((JsonArray)call["typeArgs"]).Count != 2
            || root["methods"][1][NullableRepresentationTypes.MethodFrameKey] != null
            || TypeJson.Read(call["typeArgs"][0]) != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Source-bound map helper did not retain its canonical source argument");
        Console.WriteLine("[map default binding] self-test OK (source selection, projected argument, canonical representation)");
        CollectionSelectionSelfTest();
        SemanticHelperSelfTest();
        MapCopySelfTest();
    }

    static void MapCopySelfTest()
    {
        var key = new TypeNode.Tv("method", 0);
        var value = new TypeNode.Tv("method", 1);
        var map = new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { key, value });
        var body = new JsonArray();
        var root = JsonNode.Parse("""
        {"fileClass":"kotlin.collections.ClrMapDefaultsKt","methods":[
          {"name":"clrMapPutAll","declarationId":"copy-map","static":true,"typeParams":["K","V"],
           "params":[{"name":"to","type":{"t":"fqn","name":"kotlin.Any"}},
                     {"name":"from","type":{"t":"fqn","name":"kotlin.Any"}}],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[]},
          {"name":"copy","declarationId":"copy-caller","static":true,"typeParams":["K","V"],
           "params":[],"ret":{"t":"fqn","name":"kotlin.Unit"}}]}
        """)!.AsObject();
        root["methods"][0]["body"].AsArray().Add(new JsonObject { ["k"] = "var", ["name"] = "storage", ["type"] = TypeJson.Write(map) });
        root["methods"][1]["body"] = body;
        var aliases = new Dictionary<string, string> { [map.Name] = "System.Collections.Generic.IDictionary" };
        Apply(new[] { root }, null, aliases, bind => body.Add(MemberCallSubstitution.MapCopyConstruction(
            map, new TypeNode[] { key, value }, new JsonObject { ["k"] = "callStatic", ["method"] = "ReadOnce" }, bind)));
        var call = body[0]["stmts"][2]["expr"];
        if (Text(call[DeclarationIdentityBinding.Key]) != "copy-map"
            || Text(body[0]["stmts"][0]["init"]["method"]) != "ReadOnce")
            throw new InvalidOperationException("Map copy lost source binding or source evaluation");
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false,
            policy: new GenericRepresentationPolicy(aliases));
        if (((JsonArray)call["typeArgs"]).Count != 2
            || TypeJson.Read(call["typeArgs"][0]) != key
            || TypeJson.Read(call["typeArgs"][1]) != value)
            throw new InvalidOperationException("Map copy helper changed its canonical key/value arguments");
        Console.WriteLine("[map copy binding] self-test OK (source evaluation, exact helper, canonical arguments)");
    }

    static void SemanticHelperSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var collection = new TypeNode.Fqn("kotlin.collections.Collection", new TypeNode[] { variable });
        var body = new JsonArray();
        var root = new JsonObject {
            ["fileClass"] = "kotlin.collections.ClrCollectionDefaultsKt",
            ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "clrCollToString", ["declarationId"] = "render-collection",
                    ["static"] = true, ["typeParams"] = new JsonArray("T"),
                    ["params"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(collection) }),
                    ["ret"] = TypeJson.Fqn("kotlin.String"), ["body"] = new JsonArray() },
                new JsonObject { ["name"] = "render", ["declarationId"] = "render-caller", ["static"] = true,
                    ["typeParams"] = new JsonArray("T"), ["params"] = new JsonArray(),
                    ["ret"] = TypeJson.Fqn("kotlin.String"), ["body"] = body }) };
        var aliases = new Dictionary<string, string> {
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        };
        JsonObject Render() => FaithfulHints.CollToString(new JsonObject { ["k"] = "local", ["name"] = "value" },
            FaithfulHints.CollKind.Coll, new TypeNode[] { variable });
        JsonObject Operand() => new JsonObject { ["k"] = "local", ["name"] = "value", ["sty"] = TypeJson.Write(collection) };
        Apply(new[] { root }, null, aliases, bind =>
            FaithfulHints.WithHelperBinding(bind, () => {
                body.Add(Render());
                body.Add(new JsonObject { ["k"] = "concat", ["parts"] = new JsonArray(Operand()) });
                body.Add(new JsonObject { ["k"] = "objMethod", ["method"] = "toString", ["recv"] = Operand() });
                ObjectSlotRename.Apply(root);
                FaithfulHintRecognition.Apply(root, null, new HashSet<string>());
            }));
        if (Text(body[0][DeclarationIdentityBinding.Key]) != "render-collection"
            || Text(body[1]["parts"][0][DeclarationIdentityBinding.Key]) != "render-collection"
            || Text(body[2][DeclarationIdentityBinding.Key]) != "render-collection"
            || Render()[DeclarationIdentityBinding.Key] != null)
            throw new InvalidOperationException("Semantic helper source binding escaped its lowering scope");
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false,
            policy: new GenericRepresentationPolicy(aliases));
        if (((JsonArray)body[0]["typeArgs"]).Count != 1
            || ((JsonArray)body[1]["parts"][0]["typeArgs"]).Count != 1
            || ((JsonArray)body[2]["typeArgs"]).Count != 1
            || TypeJson.Read(body[0]["typeArgs"][0]) != variable)
            throw new InvalidOperationException("Semantic helper changed its canonical argument");
        Console.WriteLine("[semantic helper binding] self-test OK (source identity, scoped binding, canonical argument)");
    }

    static void CollectionSelectionSelfTest()
    {
        const string helperOwner = "kotlin.collections.ClrCollectionDefaultsKt";
        var variable = new TypeNode.Tv("method", 0);
        var boolType = new TypeNode.Fqn("kotlin.Boolean");
        JsonObject Helper(string name) => new() {
            ["name"] = name, [DeclarationIdentityBinding.Key] = name, ["static"] = true,
            ["typeParams"] = new JsonArray("T"),
            ["params"] = new JsonArray(MemberCallSubstitution.CollectionHelperSig(helperOwner, name)
                .Select((type, index) => (JsonNode)new JsonObject { ["name"] = "p" + index, ["type"] = type.DeepClone() }).ToArray()),
            ["ret"] = TypeJson.Write(boolType), ["body"] = new JsonArray(),
        };
        JsonObject Call(string owner, TypeNode element, string member, params JsonNode[] arguments) => new() {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(new TypeNode.Fqn(owner, new[] { element })),
            ["method"] = member, ["args"] = new JsonArray(arguments),
            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "receiver" }, ["sty"] = TypeJson.Write(boolType),
        };
        var body = new JsonArray(
            Call("kotlin.collections.Collection", variable, "isEmpty"),
            Call("kotlin.collections.Collection", new TypeNode.Star(), "isEmpty"),
            Call("kotlin.collections.List", variable, "listIterator"),
            Call("kotlin.collections.MutableCollection", new TypeNode.Projection("in", variable), "add",
                new JsonObject { ["k"] = "local", ["name"] = "value" }));
        var bound = Call("kotlin.collections.Collection", variable, "isEmpty");
        bound["clrOwnerResolved"] = true;
        body.Add(bound);
        var nestedIterator = JsonNode.Parse("""
        {"k":"newObject","synthClass":{"name":"kotlin.collections.NestedIteratorProbe","methods":[
          {"name":"iterator","abstract":false,"params":[],"body":[]}]}}
        """)!;
        var iteratorOwner = new TypeNode.Fqn("kotlin.collections.NestedIteratorProbe", new TypeNode[] { variable });
        var iteratorCall = Call(iteratorOwner.Name, variable, "iterator");
        var concreteIterators = MemberCallSubstitution.CollectConcreteIteratorTypes(nestedIterator);
        if (!concreteIterators.Contains(iteratorOwner.Name)
            || MemberCallSubstitution.MissingCollectionIteratorCall(iteratorCall, iteratorOwner, new JsonArray(), true) != null
            || MemberCallSubstitution.MissingCollectionIteratorCall(iteratorCall, iteratorOwner, new JsonArray(), false) == null)
            throw new InvalidOperationException("Source iterator binding lost an unhoisted concrete declaration");
        var methods = new JsonArray(new[] { "clrCollIsEmpty", "clrProjectedCollIsEmpty", "clrListListIterator", "clrProjectedCollAdd" }
            .Select(name => (JsonNode)Helper(name)).ToArray());
        methods.Add(new JsonObject { ["name"] = "use", [DeclarationIdentityBinding.Key] = "use-collections",
            ["typeParams"] = new JsonArray("T"), ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(boolType), ["body"] = body });
        var aliases = new Dictionary<string, string> {
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
            ["kotlin.collections.List"] = "System.Collections.Generic.IReadOnlyList",
            ["kotlin.collections.MutableCollection"] = "System.Collections.Generic.ICollection",
        };
        var root = new JsonObject { ["fileClass"] = helperOwner, ["methods"] = methods,
            ["types"] = new JsonArray(aliases.Keys.Select(name => (JsonNode)new JsonObject {
                ["name"] = name, ["kind"] = "interface", ["typeParams"] = new JsonArray("T"), ["methods"] = new JsonArray(),
            }).ToArray()) };
        Apply(new[] { root }, null, aliases);
        if (Text(body[0][DeclarationIdentityBinding.Key]) != "clrCollIsEmpty"
            || Text(body[1][DeclarationIdentityBinding.Key]) != "clrProjectedCollIsEmpty"
            || Text(body[2][DeclarationIdentityBinding.Key]) != "clrListListIterator"
            || body[2]["args"][1]["value"].GetValue<int>() != 0
            || Text(body[3][DeclarationIdentityBinding.Key]) != "clrProjectedCollAdd"
            || Text(body[4]["k"]) != "callInstance")
            throw new InvalidOperationException("Source collection helper selection changed projection, default index or exact-owner routing");
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false,
            policy: new GenericRepresentationPolicy(aliases));
        if (((JsonArray)body[0]["typeArgs"]).Count != 1
            || TypeJson.Read(body[0]["typeArgs"][0]) != variable)
            throw new InvalidOperationException("Source collection helper changed its canonical argument");
        Console.WriteLine("[collection helper binding] self-test OK (invariant/projected, mutation, default index, exact owner, canonical argument)");
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;
}
