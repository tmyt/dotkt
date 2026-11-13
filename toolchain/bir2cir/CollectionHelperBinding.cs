using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Select the binding-owned Kotlin implementation while receiver arguments still denote source types.
// The resulting ordinary declaration-bound call participates in demand analysis and frame materialization;
// selecting it after erasure would require guessing source arguments from an invariant CLR storage type.
static partial class CollectionHelperBinding
{
    const string HelperOwner = "kotlin.collections.ClrMapDefaultsKt";


    public static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"fileClass":"kotlin.collections.ClrMapDefaultsKt","methods":[
          {"name":"clrMapKeys","declarationId":"map-keys","static":true,"typeParams":[{"name":"K"},{"name":"V"}],
           "params":[{"name":"m","type":{"t":"fqn","name":"kotlin.Any"}},
                     {"name":"view","type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.Any"}}}],
           "ret":{"t":"fqn","name":"kotlin.collections.Set","args":[{"t":"tv","scope":"method","i":0}]},"body":[]},
          {"name":"use","declarationId":"map-use","static":true,"typeParams":[{"name":"A"},{"name":"B"}],"params":[],
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
        ApplyFixture(root, null, aliases, CaptureFixture(root));
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
        SourceContextSelfTest();
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
                    ["static"] = true, ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }),
                    ["params"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(collection) }),
                    ["ret"] = TypeJson.Fqn("kotlin.String"), ["body"] = new JsonArray() },
                new JsonObject { ["name"] = "render", ["declarationId"] = "render-caller", ["static"] = true,
                    ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }), ["params"] = new JsonArray(),
                    ["ret"] = TypeJson.Fqn("kotlin.String"), ["body"] = body }) };
        var aliases = new Dictionary<string, string> {
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        };
        JsonObject Render() => FaithfulHints.CollToString(new JsonObject { ["k"] = "local", ["name"] = "value" },
            FaithfulHints.CollKind.Coll, new TypeNode[] { variable });
        JsonObject Operand() => new JsonObject { ["k"] = "local", ["name"] = "value", ["sty"] = TypeJson.Write(collection) };
        var typedArguments = new IBirExpression[] { new BirLocal { Name = "value" } };
        BirCallStatic typedRender = null;
        var captured = CaptureFixture(root);
        var bind = captured.Helpers.Bind;
        FaithfulHints.WithHelperBinding(bind, () => {
                typedRender = bind(new BirCallStatic {
                    Owner = new TypeNode.Fqn("kotlin.collections.ClrCollectionDefaultsKt"), Method = "clrCollToString",
                    Sig = new TypeNode[] { collection }, TypeArgs = new TypeNode[] { variable }, Args = typedArguments,
                });
                body.Add(Render());
                body.Add(new JsonObject { ["k"] = "concat", ["parts"] = new JsonArray(Operand()) });
                body.Add(new JsonObject { ["k"] = "objMethod", ["method"] = "toString", ["recv"] = Operand() });
                ObjectSlotRename.Apply(root);
                FaithfulHintRecognition.Apply(root, null, new HashSet<string>());
            });
        ApplyFixture(root, null, aliases, captured);
        if (typedRender.DeclarationId.Value != "render-collection"
            || !ReferenceEquals(typedRender.Args.Value, typedArguments)
            || typedRender.TypeArgs.Value.Single() != variable
            || Text(body[0][DeclarationIdentityBinding.Key]) != "render-collection"
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
            ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }),
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
        body.Add(Call("kotlin.collections.Set", new TypeNode.Star(), "iterator"));
        body.Add(Call("kotlin.collections.Iterable", variable, "iterator"));
        body.Add(Call("kotlin.collections.Set", variable, "contains",
            new JsonObject { ["k"] = "local", ["name"] = "value" }));
        body.Add(Call("kotlin.collections.Collection", variable, "containsAll",
            new JsonObject { ["k"] = "local", ["name"] = "values" }));
        body.Add(Call("kotlin.collections.MutableSet", variable, "contains",
            new JsonObject { ["k"] = "local", ["name"] = "value" }));
        body.Add(Call("kotlin.collections.MutableCollection", new TypeNode.Star(), "containsAll",
            new JsonObject { ["k"] = "local", ["name"] = "values" }));
        body.Add(Call("kotlin.collections.MutableList", new TypeNode.Projection("out", variable), "contains",
            new JsonObject { ["k"] = "local", ["name"] = "value" }));
        var inheritedQuery = Call("probe.QueryOwner", variable, "contains",
            new JsonObject { ["k"] = "local", ["name"] = "value" });
        inheritedQuery["overrides"] = new JsonArray(new JsonObject {
            ["owner"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Collection",
                new TypeNode[] { new TypeNode.Tv("type", 0) })),
            ["member"] = "contains", ["kind"] = "method", ["arity"] = 1,
            [DeclarationIdentityBinding.Key] = "collection-contains",
        });
        var inheritedIndex = body.Count;
        body.Add(inheritedQuery);
        var superQuery = inheritedQuery.DeepClone().AsObject();
        superQuery["super"] = true;
        body.Add(superQuery);
        var innerQuery = inheritedQuery.DeepClone().AsObject();
        innerQuery["ownerType"] = TypeJson.Write(new TypeNode.Fqn("probe.InnerQuery",
            new TypeNode[] { variable, new TypeNode.Fqn("kotlin.String") }));
        innerQuery["overrides"][0]["owner"]["args"][0] = TypeJson.Write(new TypeNode.Tv("type", 1));
        var innerIndex = body.Count;
        body.Add(innerQuery);
        var nestedIterator = JsonNode.Parse("""
        {"k":"newObject","synthClass":{"name":"kotlin.collections.NestedIteratorProbe","methods":[
          {"name":"iterator","abstract":false,"params":[],"body":[]}]}}
        """)!;
        var iteratorOwner = new TypeNode.Fqn("kotlin.collections.NestedIteratorProbe", new TypeNode[] { variable });
        var iteratorCall = Call(iteratorOwner.Name, variable, "iterator");
        iteratorCall["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator", new[] { variable }));
        iteratorCall["sty"] = iteratorCall["ret"].DeepClone();
        var abstractIterator = nestedIterator.DeepClone();
        abstractIterator["synthClass"]["name"] = "kotlin.collections.AbstractIteratorProbe";
        abstractIterator["synthClass"]["methods"][0]["abstract"] = true;
        var iteratorProperty = nestedIterator.DeepClone();
        iteratorProperty["synthClass"]["name"] = "probe.IteratorProperty";
        iteratorProperty["synthClass"]["methods"][0][KotlinPropertyAccessors.SourceNameKey] = "iterator";
        iteratorProperty["synthClass"]["methods"][0][KotlinPropertyAccessors.KindKey] = "get";
        var declaredIterators = MemberCallSubstitution.CollectDeclaredIteratorTypes(
            new JsonArray(nestedIterator, abstractIterator, iteratorProperty));
        if (!declaredIterators.Contains(iteratorOwner.Name)
            || !declaredIterators.Contains("kotlin.collections.AbstractIteratorProbe")
            || declaredIterators.Contains("probe.IteratorProperty")
            || MemberCallSubstitution.MissingCollectionIteratorCall(iteratorCall, new JsonArray(), true) != null
            || MemberCallSubstitution.MissingCollectionIteratorCall(iteratorCall, new JsonArray(), false) == null)
            throw new InvalidOperationException("Source iterator binding lost an unhoisted iterator declaration");
        var mutableCall = (JsonObject)iteratorCall.DeepClone();
        mutableCall["ownerType"] = TypeJson.Write(new TypeNode.Fqn("probe.OtherOwner", new TypeNode[] { boolType, variable }));
        mutableCall["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.MutableIterator",
            new TypeNode[] { new TypeNode.Tv("type", 0) }));
        mutableCall["sty"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.MutableIterator", new[] { variable }));
        var mutableHelper = MemberCallSubstitution.MissingCollectionIteratorCall(mutableCall, new JsonArray(), false);
        var readonlyCall = (JsonObject)mutableCall.DeepClone();
        readonlyCall["ret"] = TypeJson.Write(new TypeNode.Fqn("kotlin.collections.Iterator",
            new TypeNode[] { new TypeNode.Tv("type", 0) }));
        readonlyCall["sty"] = iteratorCall["sty"].DeepClone();
        var readonlyHelper = MemberCallSubstitution.MissingCollectionIteratorCall(readonlyCall, new JsonArray(), false);
        var genericIteratorCall = (JsonObject)mutableCall.DeepClone();
        genericIteratorCall["typeArgs"] = new JsonArray(TypeJson.Write(variable));
        var propertyCall = (JsonObject)mutableCall.DeepClone();
        propertyCall["prop"] = "get";
        var preservedPropertyCall = (JsonObject)mutableCall.DeepClone();
        KotlinPropertyAccessors.PreserveCallIdentity(preservedPropertyCall, "iterator", "get");
        if (Text(mutableHelper?["method"]) != "clrMutableIterator"
            || TypeJson.Read(mutableHelper?["typeArgs"]?[0]) != variable
            || Text(readonlyHelper?["method"]) != "iteratorOverEnumerable"
            || TypeJson.Read(readonlyHelper?["typeArgs"]?[0]) != variable
            || MemberCallSubstitution.MissingCollectionIteratorCall(genericIteratorCall, new JsonArray(), false) != null
            || MemberCallSubstitution.MissingCollectionIteratorCall(propertyCall, new JsonArray(), false) != null
            || MemberCallSubstitution.MissingCollectionIteratorCall(preservedPropertyCall, new JsonArray(), false) != null)
            throw new InvalidOperationException("Undeclared iterator binding lost its source protocol or element");
        var iteratorDefinitions = new Dictionary<string, SupertypeGraph.Def> {
            ["probe.Derived"] = new() { Base = iteratorOwner },
        };
        if (!MemberCallSubstitution.HasDeclaredIterator(new TypeNode.Fqn("probe.Derived"), declaredIterators,
                iteratorDefinitions, null, new Dictionary<string, string>())
            || MemberCallSubstitution.HasDeclaredIterator(new TypeNode.Fqn("probe.Derived"), declaredIterators,
                iteratorDefinitions, null, new Dictionary<string, string> { [iteratorOwner.Name] = "System.Object" }))
            throw new InvalidOperationException("Iterator binding lost inheritance or treated an alias as a Kotlin slot");
        var methods = new JsonArray(new[] { "clrProjectedViewIsEmpty", "clrProjectedViewIterator",
                "clrProjectedViewContains", "clrProjectedViewContainsAll", "clrProjectedViewListIterator", "clrProjectedCollAdd" }
            .Select(name => (JsonNode)Helper(name)).ToArray());
        methods.Add(new JsonObject { ["name"] = "use", [DeclarationIdentityBinding.Key] = "use-collections",
            ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }), ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(boolType), ["body"] = body });
        var aliases = new Dictionary<string, string> {
            ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
            ["kotlin.collections.List"] = "System.Collections.Generic.IReadOnlyList",
            ["kotlin.collections.MutableCollection"] = "System.Collections.Generic.ICollection",
            ["kotlin.collections.Set"] = "System.Collections.Generic.IReadOnlyCollection",
            ["kotlin.collections.MutableSet"] = "System.Collections.Generic.ISet",
            ["kotlin.collections.MutableList"] = "System.Collections.Generic.IList",
            ["kotlin.collections.Iterable"] = "System.Collections.Generic.IEnumerable",
        };
        var root = new JsonObject { ["fileClass"] = helperOwner, ["methods"] = methods,
            ["types"] = new JsonArray(aliases.Keys.Select(name => (JsonNode)new JsonObject {
                ["name"] = name, ["kind"] = "interface", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }), ["methods"] = new JsonArray(),
            }).ToArray()) };
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "probe.Outer", ["kind"] = "class", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "A" }),
        });
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "probe.InnerQuery", ["kind"] = "class", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "B" }),
            ["capturedTypeParams"] = new JsonArray(new JsonObject { ["name"] = "A" }), ["outerTypeParamCount"] = 1,
            ["semanticOwner"] = "probe.Outer", ["mods"] = new JsonObject { ["inner"] = true },
        });
        ApplyFixture(root, null, aliases, CaptureFixture(root));
        if (Text(body[0][DeclarationIdentityBinding.Key]) != "clrProjectedViewIsEmpty"
            || body[0]["args"][2]["value"].GetValue<int>() != 0
            || TypeJson.Read(body[0]["args"][1]["type"]) is not TypeNode.Fqn {
                Name: "System.Collections.Generic.IReadOnlyCollection`1", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || Text(body[1][DeclarationIdentityBinding.Key]) != "clrProjectedViewIsEmpty"
            || Text(body[1]["args"][1]["k"]) != "const"
            || Text(body[2][DeclarationIdentityBinding.Key]) != "clrProjectedViewListIterator"
            || body[2]["args"] is not JsonArray { Count: 3 }
            || body[2]["args"][2]["value"].GetValue<int>() != 3
            || Text(body[3][DeclarationIdentityBinding.Key]) != "clrProjectedCollAdd"
            || Text(body[4]["k"]) != "callInstance"
            || Text(body[5][DeclarationIdentityBinding.Key]) != "clrProjectedViewIterator"
            || body[5]["args"][2]["value"].GetValue<int>() != 1
            || Text(body[5]["args"][1]["k"]) != "const"
            || Text(body[6][DeclarationIdentityBinding.Key]) != "clrProjectedViewIterator"
            || body[6]["args"][2]["value"].GetValue<int>() != 2
            || TypeJson.Read(body[6]["args"][1]["type"]) is not TypeNode.Fqn {
                Name: "System.Collections.Generic.IEnumerable`1", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || Text(body[7][DeclarationIdentityBinding.Key]) != "clrProjectedViewContains"
            || Text(body[8][DeclarationIdentityBinding.Key]) != "clrProjectedViewContainsAll"
            || Text(body[9][DeclarationIdentityBinding.Key]) != "clrProjectedViewContains"
            || body[9]["args"][2]["value"].GetValue<int>() != 4
            || TypeJson.Read(body[9]["args"][1]["type"]) is not TypeNode.Fqn {
                Name: "System.Collections.Generic.ISet`1", Args: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || Text(body[10][DeclarationIdentityBinding.Key]) != "clrProjectedViewContainsAll"
            || body[10]["args"][2]["value"].GetValue<int>() != 5
            || Text(body[10]["args"][1]["k"]) != "const"
            || Text(body[11][DeclarationIdentityBinding.Key]) != "clrProjectedViewContains"
            || body[11]["args"][2]["value"].GetValue<int>() != 6
            || Text(body[inheritedIndex][DeclarationIdentityBinding.Key]) != "clrProjectedViewContains"
            || TypeJson.Read(body[inheritedIndex]["typeArgs"][0]) != variable
            || Text(body[inheritedIndex + 1]["k"]) != "callInstance"
            || TypeJson.Read(body[innerIndex]["typeArgs"][0]) != variable)
            throw new InvalidOperationException("Source collection helper selection changed projection, default index or exact-owner routing");
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false,
            policy: new GenericRepresentationPolicy(aliases));
        if (((JsonArray)body[0]["typeArgs"]).Count != 1
            || TypeJson.Read(body[0]["typeArgs"][0]) != variable)
            throw new InvalidOperationException("Source collection helper changed its canonical argument");
        Console.WriteLine("[collection helper binding] self-test OK (static family/preference, source selection, mutation, default index, exact owner, canonical argument)");
    }

    // These DOM-consumer fixtures keep direct expression handles for assertions.
    // Author their statement wrappers only in the owning capture fixture, before
    // helper preparation or collection binding mutates the original graph.
    static SourceCollectionInputs CaptureFixture(JsonObject root)
        => new(new[] { ReadFixture(root) }, null);

    static BirFileDeclaration ReadFixture(JsonObject root)
    {
        var source = root.DeepClone().AsObject();
        foreach (var method in (source["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (method["body"] is JsonArray body)
                for (var index = 0; index < body.Count; index++)
                {
                    var expression = body[index];
                    body[index] = null;
                    body[index] = new JsonObject { ["k"] = "exprStmt", ["expr"] = expression };
                }
        return BirGraphDom.ReadBirFileDeclaration(source);
    }

    // Only fixture input/output is adapted: production never sends executable
    // bodies across a temporary DOM boundary to call this owning consumer.
    static void ApplyFixture(JsonObject root, ReferenceMetadataIndex references,
        IReadOnlyDictionary<string, string> aliases, SourceCollectionInputs captured)
    {
        var source = ReadFixture(root);
        Apply(new[] { source }, references, aliases, captured);
        var methods = root["methods"].AsArray();
        for (var method = 0; method < source.Methods.Value.Length; method++)
        {
            if (methods[method]?["body"] is not JsonArray body) continue;
            var statements = source.Methods.Value[method].Body.OrEmpty();
            for (var index = 0; index < statements.Length; index++)
                body[index] = ((BirGraphValue)((BirExpressionStatement)statements[index]).Expr.Value).ToDom();
        }
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;
}
