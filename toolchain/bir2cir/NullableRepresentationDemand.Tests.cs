using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class NullableRepresentationDemand
{
    public static void SelfTest()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Nullable representation self-test: " + message);
        }
        static void Malformed(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Malformed current nullable frame was accepted");
        }

        var frame = new NullableRepresentationFrame(2, new[] { 1 });
        Check(frame.PhysicalArity == 3, "physical arity");
        Check(frame.NullableVariable(new TypeNode.Tv("type", 1)) == new TypeNode.Tv("type", 2), "owner scope");
        Check(frame.NullableVariable(new TypeNode.Tv("method", 1)) == new TypeNode.Tv("method", 2), "method scope");
        var restored = NullableRepresentationFrame.Read(frame.ToJson());
        Check(restored.SourceArity == 2 && restored.NullableIndices.SequenceEqual(new[] { 1 }), "metadata correspondence");
        Check(restored.SemanticVariable(new TypeNode.Tv("method", 2)) == new TypeNode.Nullable(new TypeNode.Tv("method", 1)),
            "physical companion restores nullable source method variable");
        Check(restored.SemanticVariable(new TypeNode.Tv("type", 2)) == new TypeNode.Nullable(new TypeNode.Tv("type", 1)),
            "physical companion restores nullable source owner variable");
        Check(restored.SemanticVariable(new TypeNode.Tv("type", 0)) == new TypeNode.Tv("type", 0),
            "ordinary source variable identity is retained");
        var stringType = new TypeNode.Fqn("kotlin.String");
        var nullableString = new TypeNode.Nullable(stringType);
        var closed = frame.Close(new TypeNode[] { stringType, nullableString },
            _ => new TypeNode.Fqn("object"), source => source);
        Check(closed[2] == nullableString, "nullable closure must receive original source argument");
        Check(restored.OrdinaryArguments(closed).Length == 2, "physical companions are hidden using explicit frame metadata");
        Malformed(() => restored.SemanticVariable(new TypeNode.Tv("type", 3)));
        Malformed(() => restored.OrdinaryArguments(new TypeNode[] { stringType }));
        Malformed(() => new NullableRepresentationFrame(1, new[] { 1 }));
        Malformed(() => new NullableRepresentationFrame(2, new[] { 1, 0 }));
        Malformed(() => new NullableRepresentationFrame(2, new[] { 1, 1 }));
        Malformed(() => NullableRepresentationFrame.Read(new JsonObject { ["sourceArity"] = 1 }));

        // An outer T, N(T) prefix precedes the child's own U. Source order remains T, U.
        var nested = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 0, 2, 1 });
        var nestedRoundtrip = NullableRepresentationFrame.Read(nested.ToJson());
        Check(nestedRoundtrip.SourcePosition(1) == 2, "nested ordinary slot follows enclosing companion");
        Check(nestedRoundtrip.SourceIndex(1) == null && nestedRoundtrip.SourceIndex(2) == 1,
            "physical slots classify by correspondence, not source arity");
        Check(nestedRoundtrip.NullableVariable(new TypeNode.Tv("type", 0)) == new TypeNode.Tv("type", 1),
            "nested companion keeps enclosing prefix");
        Check(nestedRoundtrip.SemanticVariable(new TypeNode.Tv("type", 2)) == new TypeNode.Tv("type", 1),
            "nested ordinary slot restores source identity");
        var nestedClosed = nestedRoundtrip.Close(new TypeNode[] { stringType, new TypeNode.Fqn("kotlin.Int") },
            type => type, type => new TypeNode.Nullable(type));
        Check(nestedClosed[1] == nullableString && nestedClosed[2] == new TypeNode.Fqn("kotlin.Int"),
            "nested applications follow physical order");
        Check(nestedRoundtrip.OrdinaryArguments(nestedClosed).SequenceEqual(new TypeNode[] {
            stringType, new TypeNode.Fqn("kotlin.Int") }), "nested source arguments are not a physical prefix");
        Malformed(() => new NullableRepresentationFrame(2, new[] { 0 }, new[] { 0, 2, 2 }));

        // Root and invariant-storage applications of one source parameter are independent closures. In
        // particular, nullable-storage must not receive an already erased ordinary or nullable argument.
        var roles = new NullableRepresentationFrame(2, new[] { 0 }, storageIndices: new[] { 0, 1 },
            nullableStorageIndices: new[] { 0 });
        var roleFrame = NullableRepresentationFrame.Read(roles.ToJson());
        Check(roleFrame.PhysicalArity == 6 && roleFrame.StorageIndices.SequenceEqual(new[] { 0, 1 })
            && roleFrame.NullableStorageIndices.SequenceEqual(new[] { 0 }), "representation roles survive metadata");
        var roleArguments = roleFrame.Close(new TypeNode[] { stringType, nullableString },
            source => new TypeNode.Fqn("Root", new[] { source }),
            source => new TypeNode.Fqn("NullableRoot", new[] { source }),
            source => new TypeNode.Fqn("Storage", new[] { source }),
            source => new TypeNode.Fqn("NullableStorage", new[] { source }));
        Check(roleArguments[3] == new TypeNode.Fqn("Storage", new[] { stringType })
            && roleArguments[4] == new TypeNode.Fqn("Storage", new TypeNode[] { nullableString })
            && roleArguments[5] == new TypeNode.Fqn("NullableStorage", new[] { stringType }),
            "all representation closures receive source arguments");
        Check(roleFrame.Variable(new TypeNode.Tv("method", 0), NullableRepresentationFrame.Role.Storage)
            == new TypeNode.Tv("method", 3), "storage variable has declaration scope");
        Check(roleFrame.SemanticVariable(new TypeNode.Tv("method", 3)) == new TypeNode.Tv("method", 0)
            && roleFrame.SemanticVariable(new TypeNode.Tv("type", 5))
                == new TypeNode.Nullable(new TypeNode.Tv("type", 0)), "storage companions restore source vocabulary");
        Check(roleFrame.OrdinaryArguments(roleArguments).SequenceEqual(roleArguments.Take(2)),
            "storage companions do not become source arguments");
        Malformed(() => roleFrame.Close(new TypeNode[] { stringType, stringType }, source => source, source => source));
        Malformed(() => new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 1 }));
        Malformed(() => new NullableRepresentationFrame(1, Array.Empty<int>(), nullableStorageIndices: new[] { 0, 0 }));
        var malformedRoles = roleFrame.ToJson();
        malformedRoles["storage"] = new JsonArray("invalid");
        Malformed(() => NullableRepresentationFrame.Read(malformedRoles));

        var outerRoles = new NullableRepresentationFrame(1, new[] { 0 }, storageIndices: new[] { 0 },
            nullableStorageIndices: new[] { 0 });
        // The child's own U precedes captured T in SOURCE order. CLR still needs all four outer slots first.
        var capturedRoles = new NullableRepresentationFrame(2, new[] { 1 }, storageIndices: new[] { 1 },
            nullableStorageIndices: new[] { 1 }).WithEnclosingPrefix(outerRoles, 1);
        Check(capturedRoles.PhysicalOrder.SequenceEqual(new[] { 1, 2, 3, 4, 0 }),
            "enclosing prefix includes each representation of captured T before U");
        Check(capturedRoles.SourcePosition(0) == 4 && capturedRoles.SourcePosition(1) == 0
            && capturedRoles.SourceIndex(2) == null, "captured source identity is independent of physical role");
        Check(capturedRoles.Variable(new TypeNode.Tv("type", 1), NullableRepresentationFrame.Role.NullableStorage)
            == new TypeNode.Tv("type", 3), "captured nullable-storage position");
        Malformed(() => frame.WithEnclosingPrefix(outerRoles, 0));
        Malformed(() => capturedRoles.WithEnclosingPrefix(outerRoles, 2));
        var retainedOuter = capturedRoles.RetainSources(new[] { 1 });
        Check(retainedOuter.PhysicalArity == 4 && retainedOuter.StorageIndices.SequenceEqual(new[] { 0 })
            && retainedOuter.NullableStorageIndices.SequenceEqual(new[] { 0 })
            && retainedOuter.PhysicalOrder.SequenceEqual(Enumerable.Range(0, 4)),
            "capture pruning retains all roles of a source variable");
        var retainedOwn = capturedRoles.RetainSources(new[] { 0 });
        Check(retainedOwn.PhysicalArity == 1 && retainedOwn.SourcePosition(0) == 0,
            "capture pruning removes complete companion groups");
        var reordered = roles.RetainSources(new[] { 1, 0 });
        Check(reordered.PhysicalOrder.SequenceEqual(new[] { 1, 0, 2, 4, 3, 5 })
            && reordered.NullableIndices.SequenceEqual(new[] { 1 })
            && reordered.NullableStorageIndices.SequenceEqual(new[] { 1 }), "source renumbering preserves physical order");
        Malformed(() => roles.RetainSources(new[] { 0, 0 }));

        static JsonNode Tv(string scope = "type", int index = 0) => TypeJson.Write(new TypeNode.Tv(scope, index));
        static JsonNode NullableTv(string scope = "type") => new JsonObject { ["t"] = "nullable", ["of"] = Tv(scope) };
        static JsonNode Applied(string name, JsonNode argument) => new JsonObject {
            ["t"] = "fqn", ["name"] = name, ["args"] = new JsonArray(argument),
        };
        static JsonObject Owner(string name, JsonNode field) => new() {
            ["kind"] = "class", ["name"] = name, ["typeParams"] = new JsonArray("T"),
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = field }),
        };
        static JsonObject Method(string id, JsonNode result, JsonArray body) => new() {
            ["name"] = id, [DeclarationIdentityBinding.Key] = id,
            ["typeParams"] = new JsonArray("U"), ["params"] = new JsonArray(), ["ret"] = result, ["body"] = body,
        };

        var box = Owner("Box", Tv());
        var exchange = Owner("Exchange", Applied("Box", NullableTv()));
        var wrapper = Owner("Wrapper", Applied("Exchange", Tv()));
        var outer = Owner("Outer", Applied("Wrapper", Tv()));
        var scalar = Owner("Scalar", NullableTv());
        var callee = Method("callee", Applied("Box", NullableTv("method")), new JsonArray());
        var caller = Method("caller", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "callee", ["typeArgs"] = new JsonArray(Tv("method")),
        }));
        caller["virtual"] = true;
        var arrayBody = Method("arrayBody", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "newArraySized", ["elem"] = NullableTv("method"),
        }));
        var refBody = Method("refBody", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "byrefLoad", ["elem"] = NullableTv("method"),
        }));
        var callFromCtor = Owner("BodyOnly", Tv());
        callFromCtor["ctors"] = new JsonArray(new JsonObject {
            ["params"] = new JsonArray(), ["body"] = new JsonArray(new JsonObject {
                ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "callee", ["typeArgs"] = new JsonArray(Tv()),
            }),
        });
        var usesBodyOwner = Owner("UsesBodyOwner", Applied("BodyOnly", Tv()));
        var file = new JsonObject {
            ["fileClass"] = "FrameTests", ["types"] = new JsonArray(outer, wrapper, exchange, box, scalar, callFromCtor, usesBodyOwner),
            ["methods"] = new JsonArray(caller, callee, arrayBody, refBody),
        };
        var original = file.ToJsonString();
        var demands = Collect(new[] { file });
        OwnerDemand Find(JsonObject declaration) => demands.Single(owner => ReferenceEquals(owner.Declaration, declaration));
        Check(Find(box).Frame.PhysicalArity == 1, "ordinary invariant type stays exact");
        Check(Find(scalar).Frame.PhysicalArity == 1, "direct nullable scalar does not require reified argument frame");
        Check(Find(exchange).Frame.NullableIndices.SequenceEqual(new[] { 0 }), "direct constructed demand");
        Check(Find(outer).Frame.NullableIndices.SequenceEqual(new[] { 0 }), "transitive construction fixed point");
        var callerDemand = Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, caller));
        Check(callerDemand.Frame.PhysicalArity == 1 && callerDemand.Body.Method.SetEquals(new[] { 0 }),
            "body-only call demand must not alter virtual declaration arity");
        Check(Find(callFromCtor).Frame.PhysicalArity == 2 && Find(callFromCtor).Body.Type.SetEquals(new[] { 0 })
            && Find(callFromCtor).Signature.Type.Count == 0,
            "constructor implementation demand contributes to the owned TypeDef frame without becoming a source signature fact");
        Check(Find(usesBodyOwner).Frame.NullableIndices.SequenceEqual(new[] { 0 }),
            "body-only owner arguments propagate through constructed type applications");
        Check(Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, arrayBody)).Body.Method.SetEquals(new[] { 0 }),
            "array element is a reified argument position even without a container type");
        Check(Find(file).Methods.Single(method => ReferenceEquals(method.Declaration, refBody)).Body.Method.Count == 0,
            "managed reference referent is a direct scalar slot");
        Check(file.ToJsonString() == original, "analysis must preserve all source facts");

        // Static implementation demand propagates through calls without changing a fixed instance/virtual slot.
        var freeArray = Method("freeArray", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "newArraySized", ["elem"] = NullableTv("method"),
        }));
        freeArray["static"] = true;
        var freeCaller = Method("freeCaller", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "freeArray", ["typeArgs"] = new JsonArray(Tv("method")),
        }));
        freeCaller["static"] = true;
        var fixedCaller = Method("fixedCaller", TypeJson.Fqn("kotlin.Unit"), new JsonArray(new JsonObject {
            ["k"] = "callStatic", [DeclarationIdentityBinding.Key] = "freeCaller", ["typeArgs"] = new JsonArray(Tv("method")),
        }));
        fixedCaller["static"] = false;
        fixedCaller["virtual"] = true;
        var freeDemands = Collect(new[] { new JsonObject { ["fileClass"] = "FreeFrame",
            ["methods"] = new JsonArray(fixedCaller, freeCaller, freeArray) } }).Single().Methods;
        Check(freeDemands.Single(m => ReferenceEquals(m.Declaration, freeArray)).Frame.PhysicalArity == 2,
            "static body-only demand owns a method frame");
        Check(freeDemands.Single(m => ReferenceEquals(m.Declaration, freeCaller)).Frame.PhysicalArity == 2,
            "static body-only call demand reaches a fixed point");
        var fixedDemand = freeDemands.Single(m => ReferenceEquals(m.Declaration, fixedCaller));
        Check(fixedDemand.Frame.PhysicalArity == 1 && fixedDemand.Body.Method.SetEquals(new[] { 0 }),
            "static body-only propagation must not grow an instance dispatch slot");
        freeCaller["static"] = false;
        var instanceDemand = Collect(new[] { freeCaller.Parent.Parent }).Single().Methods
            .Single(m => ReferenceEquals(m.Declaration, freeCaller));
        Check(instanceDemand.Frame.PhysicalArity == 2,
            "independent instance body owns its frame without a private dispatch helper");

        var importedUse = Owner("ImportedUse", Applied("ForeignProducer", Tv()));
        var imported = Collect(new[] { importedUse }, new Dictionary<string, NullableRepresentationFrame> {
            ["ForeignProducer"] = new NullableRepresentationFrame(1, new[] { 0 }),
        });
        Check(imported[0].Frame.NullableIndices.SequenceEqual(new[] { 0 }), "referenced declaration correspondence");
        var constrainedInherited = JsonNode.Parse("""
        {"kind":"class","name":"ConstraintUser","inheritedDefaultMethods":[{
          "member":"use","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},
          "implementation":{"owner":{"t":"fqn","name":"Foreign"},"member":"use","arity":2,
            "typeParams":["T",{"name":"U","constraints":[
              {"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]},
              {"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":4}}]}
            ]}]}}]}
        """);
        var constrainedDemand = Collect(new[] { constrainedInherited }).Single();
        Check(constrainedDemand.Methods.Single().Frame.NullableIndices.SequenceEqual(new[] { 0 })
            && constrainedDemand.Frame.PhysicalArity == 0,
            "inherited implementation constraints contribute method demand without importing the foreign owner's frame");
        StorageDemandSelfTest();
        MetadataSelfTest();
        Console.WriteLine("[nullable representation frame] self-test OK (source correspondence, scopes, declaration/body demand, fixed point)");
    }

    static void StorageDemandSelfTest()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Storage representation demand self-test: " + message);
        }
        var importedFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 },
            nullableStorageIndices: new[] { 0 });
        var types = new Dictionary<string, NullableRepresentationFrame> { ["StorageSource"] = importedFrame };
        var methods = new Dictionary<string, NullableRepresentationFrame> { ["storage-source"] = importedFrame };
        var root = JsonNode.Parse("""
        {"fileClass":"StorageCalls","methods":[
          {"name":"virtualUse","declarationId":"virtual-use","virtual":true,"typeParams":["T"],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","declarationId":"forward","typeArgs":[{"t":"tv","scope":"method","i":0}]}]},
          {"name":"forward","declarationId":"forward","typeParams":["T"],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","declarationId":"leaf","typeArgs":[{"t":"tv","scope":"method","i":0}]}]},
          {"name":"leaf","declarationId":"leaf","typeParams":["T"],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","declarationId":"storage-source","typeArgs":[{"t":"tv","scope":"method","i":0}]}]},
          {"name":"nullableForward","typeParams":["T"],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"callStatic","declarationId":"storage-source","typeArgs":[
              {"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]}]}],
         "types":[
          {"kind":"class","name":"StorageOuter","typeParams":["T"],"fields":[
            {"name":"value","type":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"type","i":0}]}}],
           "types":[{"kind":"class","name":"StorageNested","typeParams":["U","T"],
             "semanticOwner":"StorageOuter","outerTypeParamCount":1,"outerTypeParamOffset":1}]},
          {"kind":"class","name":"StorageIndirect","typeParams":["T"],"fields":[
            {"name":"value","type":{"t":"fqn","name":"StorageOuter","args":[{"t":"tv","scope":"type","i":0}]}}]},
          {"kind":"class","name":"StorageScope","typeParams":["T"],"methods":[
            {"name":"both","typeParams":["T"],"params":[{"name":"owner","type":
              {"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"type","i":0}]}}],
             "ret":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"method","i":0}]},"body":[]}]}]}
        """)!.AsObject();
        var original = root.ToJsonString();
        var demands = Collect(new[] { root }, types, methods);
        var calls = demands.Single(owner => ReferenceEquals(owner.Declaration, root)).Methods;
        foreach (var name in new[] { "leaf", "forward" })
        {
            var method = calls.Single(method => Text(method.Declaration["name"]) == name);
            Check(method.Frame.StorageIndices.SequenceEqual(new[] { 0 })
                && method.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 }) && method.Frame.PhysicalArity == 3,
                "body-only role demand reaches nonvirtual callers: " + name);
        }
        var virtualUse = calls.Single(method => Text(method.Declaration["name"]) == "virtualUse");
        Check(virtualUse.Frame.PhysicalArity == 1
            && virtualUse.Body.For("method", NullableRepresentationFrame.Role.Storage).SetEquals(new[] { 0 })
            && virtualUse.Body.For("method", NullableRepresentationFrame.Role.NullableStorage).SetEquals(new[] { 0 }),
            "virtual body storage demand does not change its dispatch ABI");
        var nullableUse = calls.Single(method => Text(method.Declaration["name"]) == "nullableForward");
        Check(nullableUse.Frame.StorageIndices.Count == 0
            && nullableUse.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 })
            && nullableUse.Frame.NullableIndices.SequenceEqual(new[] { 0 }),
            "storage of a nullable argument demands nullable-storage alongside its ordinary nullable form");
        var indirect = demands.Single(owner => Text(owner.Declaration["name"]) == "StorageIndirect");
        Check(indirect.Frame.StorageIndices.SequenceEqual(new[] { 0 })
            && indirect.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 }), "type application fixed point retains roles");
        var nested = demands.Single(owner => Text(owner.Declaration["name"]) == "StorageNested");
        Check(nested.Frame.PhysicalOrder.SequenceEqual(new[] { 1, 2, 3, 0 })
            && nested.Frame.StorageIndices.SequenceEqual(new[] { 1 })
            && nested.Frame.NullableStorageIndices.SequenceEqual(new[] { 1 }), "enclosing captures retain storage role groups");
        var scoped = demands.Single(owner => Text(owner.Declaration["name"]) == "StorageScope");
        Check(scoped.Frame.PhysicalArity == 3 && scoped.Methods.Single().Frame.PhysicalArity == 3,
            "method and owner source index zero remain independent");
        Check(root.ToJsonString() == original, "role analysis does not rewrite declarations");
        var localRoot = JsonNode.Parse("""
        {"fileClass":"LocalStorage","methods":[{"name":"entry","typeParams":["U"],"params":[],
          "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"localFun","id":"local-storage","decl":{"name":"local","typeParams":["T"],
              "_syntheticTypeArgs":[{"t":"tv","scope":"method","i":0}],"params":[],
              "ret":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"method","i":0}]},"body":[]}},
            {"k":"callLocal","id":"local-storage","typeArgs":[{"t":"tv","scope":"method","i":0}],"args":[]}]}]}
        """)!.AsObject();
        var locals = Collect(new[] { localRoot }, types, methods).Single().Methods;
        Check(locals.Single(method => Text(method.Declaration["name"]) == "entry").Frame.PhysicalArity == 3,
            "local call explicitly relates its storage demand to the lexical method frame");
        ((JsonArray)localRoot["methods"][0]["body"]).RemoveAt(1);
        locals = Collect(new[] { localRoot }, types, methods).Single().Methods;
        Check(locals.Single(method => Text(method.Declaration["name"]) == "entry").Frame.PhysicalArity == 1
            && locals.Single(method => method.IsLocal).Frame.PhysicalArity == 3,
            "a local function's own method index is not an implicit lexical capture");
    }

    static void MetadataSelfTest()
    {
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var owner = new JsonObject {
            ["kind"] = "class", ["name"] = "FrameOwner", ["typeParams"] = new JsonArray("T", "N"),
        };
        KotlinSupertypesRecord.Merge(owner, new JsonObject { [NullableRepresentationFrame.MetadataKey] = frame.ToJson() });
        KotlinSupertypesRecord.Merge(owner, new JsonObject {
            ["bounds"] = new JsonObject { ["0"] = new JsonArray(TypeJson.Fqn("System.Object")) },
        });
        var sourceResult = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) });
        var method = new JsonObject {
            ["name"] = "physical", [DeclarationIdentityBinding.Key] = "frame-test-method", ["declarationSourceName"] = "source",
            ["typeParams"] = new JsonArray("T", "N"), ["params"] = new JsonArray(),
            ["ret"] = TypeJson.Write(new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 1) })),
            [DeclarationIdentityBinding.SemanticSignatureKey] = new JsonObject {
                ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(sourceResult),
            },
            [NullableRepresentationTypes.MethodFrameKey] = frame.ToJson().ToJsonString(),
        };
        var root = new JsonObject { ["fileClass"] = "FrameFile", ["types"] = new JsonArray(owner), ["methods"] = new JsonArray(method) };
        var runtime = root.DeepClone();
        DeclarationIdentityBinding.ApplyLocal(new[] { root },
            new Dictionary<string, string> { ["frame-test-method"] = "physical" }, new HashSet<string>(),
            new Dictionary<string, JsonObject> {
                ["frame-test-method"] = (JsonObject)method[DeclarationIdentityBinding.SemanticSignatureKey].DeepClone(),
            }, new Dictionary<string, int[]>(), null);
        RoundtripMetadata.Stamp(root);

        static JsonNode Payload(JsonObject declaration, string name)
        {
            var attribute = ((JsonArray)declaration["attrs"]).OfType<JsonObject>().Single(attr =>
                TypeJson.OwnerName(attr["attr"]) == "DotKt.Runtime.CompilerServices." + name);
            var bytes = attribute["args"][1]["bytes"].GetValue<string>();
            return BirCarrier.DecodeBody(BirCarrier.JsonV1, Convert.FromBase64String(bytes));
        }
        var typePayload = Payload(owner, "KotlinSupertypesAttribute");
        var methodPayload = Payload(method, "KotlinDeclarationIdentityAttribute");
        if (!JsonNode.DeepEquals(typePayload[NullableRepresentationFrame.MetadataKey], frame.ToJson())
            || typePayload["bounds"] == null
            || !JsonNode.DeepEquals(methodPayload[NullableRepresentationFrame.MetadataKey], frame.ToJson())
            || TypeJson.Read(methodPayload["signature"]["ret"]) != sourceResult
            || owner[KotlinSupertypesRecord.PreKey] != null || method[NullableRepresentationTypes.MethodFrameKey] != null)
            throw new InvalidOperationException("Nullable representation metadata lost source facts or leaked transient frames");
        RoundtripMetadata.StripRuntimeAttrs(runtime);
        if (runtime["types"][0][KotlinSupertypesRecord.PreKey] != null
            || runtime["methods"][0][NullableRepresentationTypes.MethodFrameKey] != null)
            throw new InvalidOperationException("Runtime metadata stripping leaked nullable representation facts");
    }
}
