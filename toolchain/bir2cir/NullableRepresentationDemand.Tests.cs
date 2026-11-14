using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

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
        var firstFrame = new NullableRepresentationFrame(2, new[] { 1 }, new[] { 3, 1, 2, 0 },
            storageIndices: new[] { 0 });
        var secondFrame = new NullableRepresentationFrame(2, new[] { 0 }, new[] { 4, 1, 3, 0, 2 },
            storageIndices: new[] { 1 }, nullableStorageIndices: new[] { 0 });
        var composedFrame = firstFrame.Concat(secondFrame);
        Check(composedFrame.SourceArity == 4 && composedFrame.PhysicalArity == 9
            && composedFrame.PhysicalOrder.SequenceEqual(new[] { 6, 1, 4, 0, 8, 3, 7, 2, 5 })
            && composedFrame.PhysicalSlot(0) == new NullableRepresentationFrame.Slot(0, NullableRepresentationFrame.Role.Storage)
            && composedFrame.PhysicalSlot(4) == new NullableRepresentationFrame.Slot(2, NullableRepresentationFrame.Role.NullableStorage)
            && composedFrame.PhysicalSlot(6) == new NullableRepresentationFrame.Slot(3, NullableRepresentationFrame.Role.Storage),
            "composition preserves each producer's source identity, companion role and physical permutation");
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

        static TypeNode Tv(string scope = "type", int index = 0) => new TypeNode.Tv(scope, index);
        static TypeNode NullableTv(string scope = "type") => new TypeNode.Nullable(Tv(scope));
        static TypeNode Applied(string name, TypeNode argument) => new TypeNode.Fqn(name, new[] { argument });
        static BirClassDefinition Owner(string name, TypeNode field) => new() {
            Kind = "class", Name = name, TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { new BirFieldDeclaration { Name = "value", Vis = "private", Type = field } },
        };
        static BirFunctionDeclaration Method(string id, TypeNode result, params IBirExpression[] body) => new() {
            Name = id, DeclarationId = id, TypeParams = new[] { new BirTypeParameter { Name = "U" } },
            Params = Array.Empty<BirParameter>(), Ret = result,
            Body = body.Select(expression => (IBirStatement)new BirExpressionStatement {
                Expr = OptionalField<IBirExpression>.Of(expression),
            }).ToArray(),
        };

        var box = Owner("Box", Tv());
        var exchange = Owner("Exchange", Applied("Box", NullableTv()));
        var wrapper = Owner("Wrapper", Applied("Exchange", Tv()));
        var outer = Owner("Outer", Applied("Wrapper", Tv()));
        var scalar = Owner("Scalar", NullableTv());
        var callee = Method("callee", Applied("Box", NullableTv("method")));
        var caller = Method("caller", new TypeNode.Fqn("kotlin.Unit"), new BirCallStatic {
            DeclarationId = "callee", TypeArgs = new[] { Tv("method") },
        });
        caller.Virtual = true;
        var arrayBody = Method("arrayBody", new TypeNode.Fqn("kotlin.Unit"), new BirNewArraySized { Elem = NullableTv("method") });
        var refBody = Method("refBody", new TypeNode.Fqn("kotlin.Unit"), new BirByRefLoad { Elem = NullableTv("method") });
        var callFromCtor = Owner("BodyOnly", Tv());
        callFromCtor.Ctors = new[] { new BirConstructor {
            Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirExpressionStatement {
                Expr = new BirCallStatic { DeclarationId = "callee", TypeArgs = new[] { Tv() } },
            } },
        } };
        var usesBodyOwner = Owner("UsesBodyOwner", Applied("BodyOnly", Tv()));
        var file = new BirFileDeclaration {
            FileClass = "FrameTests", Types = new IBirTypeDeclaration[] { outer, wrapper, exchange, box, scalar, callFromCtor, usesBodyOwner },
            Methods = new[] { caller, callee, arrayBody, refBody },
        };
        var original = file.DeepClone();
        var demands = Collect(new[] { file });
        SourceOwnerDemand Find(BirGraphValue declaration) => demands.Single(owner => ReferenceEquals(owner.Declaration, declaration));
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
        Check(file.StructurallyEquals(original), "analysis must preserve all source facts");

        // Static implementation demand propagates through calls without changing a fixed instance/virtual slot.
        var freeArray = Method("freeArray", new TypeNode.Fqn("kotlin.Unit"), new BirNewArraySized { Elem = NullableTv("method") });
        freeArray.Static = true;
        var freeCaller = Method("freeCaller", new TypeNode.Fqn("kotlin.Unit"), new BirCallStatic {
            DeclarationId = "freeArray", TypeArgs = new[] { Tv("method") },
        });
        freeCaller.Static = true;
        var fixedCaller = Method("fixedCaller", new TypeNode.Fqn("kotlin.Unit"), new BirCallStatic {
            DeclarationId = "freeCaller", TypeArgs = new[] { Tv("method") },
        });
        fixedCaller.Static = false;
        fixedCaller.Virtual = true;
        var freeFile = new BirFileDeclaration { FileClass = "FreeFrame", Methods = new[] { fixedCaller, freeCaller, freeArray } };
        var freeDemands = Collect(new[] { freeFile }).Single().Methods;
        Check(freeDemands.Single(m => ReferenceEquals(m.Declaration, freeArray)).Frame.PhysicalArity == 2,
            "static body-only demand owns a method frame");
        Check(freeDemands.Single(m => ReferenceEquals(m.Declaration, freeCaller)).Frame.PhysicalArity == 2,
            "static body-only call demand reaches a fixed point");
        var fixedDemand = freeDemands.Single(m => ReferenceEquals(m.Declaration, fixedCaller));
        Check(fixedDemand.Frame.PhysicalArity == 1 && fixedDemand.Body.Method.SetEquals(new[] { 0 }),
            "static body-only propagation must not grow an instance dispatch slot");
        freeCaller.Static = false;
        var instanceDemand = Collect(new[] { freeFile }).Single().Methods
            .Single(m => ReferenceEquals(m.Declaration, freeCaller));
        Check(instanceDemand.Frame.PhysicalArity == 2,
            "independent instance body owns its frame without a private dispatch helper");

        var importedUse = Owner("ImportedUse", Applied("ForeignProducer", Tv()));
        var publishedThin = Method("publishedThin", new TypeNode.Fqn("kotlin.Unit"));
        publishedThin.Static = true;
        var thinCaller = Method("thinCaller", new TypeNode.Fqn("kotlin.Unit"), new BirCallStatic {
            DeclarationId = "publishedThin", TypeArgs = new[] { Tv("method") },
        });
        thinCaller.Static = true;
        var published = Collect(new[] { new BirFileDeclaration {
            FileClass = "PublishedFrame", Methods = new[] { publishedThin, thinCaller },
        } }, declaredMethods: new Dictionary<string, NullableRepresentationFrame> {
            ["publishedThin"] = new(1, Array.Empty<int>(), storageIndices: new[] { 0 }),
        }).Single().Methods;
        Check(published.Single(method => ReferenceEquals(method.Declaration, publishedThin)).Frame.PhysicalArity == 2,
            "a thin definition retains the exact published declaration frame");
        Check(published.Single(method => ReferenceEquals(method.Declaration, thinCaller)).Frame.PhysicalArity == 2,
            "calls of a thin local definition propagate its published frame");
        var unrelated = (BirFunctionDeclaration)publishedThin.DeepClone();
        var unpaired = Collect(new[] { new BirFileDeclaration {
            FileClass = "UnpairedDefinition", Methods = new[] { unrelated },
        } }, referencedMethods: new Dictionary<string, NullableRepresentationFrame> {
            ["publishedThin"] = new(1, Array.Empty<int>(), storageIndices: new[] { 0 }),
        }).Single().Methods.Single();
        Check(unpaired.Frame.PhysicalArity == 1,
            "an ordinary definition does not inherit an unrelated referenced declaration frame");
        var imported = Collect(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { importedUse } } }, new Dictionary<string, NullableRepresentationFrame> {
            ["ForeignProducer"] = new NullableRepresentationFrame(1, new[] { 0 }),
        });
        Check(imported.Single(owner => ReferenceEquals(owner.Declaration, importedUse)).Frame.NullableIndices.SequenceEqual(new[] { 0 }), "referenced declaration correspondence");
        var constrainedInherited = BirGraphDom.ReadBirClassDefinition(JsonNode.Parse("""
        {"kind":"class","name":"ConstraintUser","inheritedDefaultMethods":[{
          "member":"use","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},
          "implementation":{"owner":{"t":"fqn","name":"Foreign"},"member":"use","arity":2,
            "typeParams":[{"name":"T"},{"name":"U","constraints":[
              {"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]},
              {"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":4}}]}
            ]}]}}]}
        """));
        var constrainedDemand = Collect(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] { constrainedInherited } } })
            .Single(owner => ReferenceEquals(owner.Declaration, constrainedInherited));
        Check(constrainedDemand.Methods.Single().Frame.NullableIndices.SequenceEqual(new[] { 0 })
            && constrainedDemand.Frame.PhysicalArity == 0,
            "inherited implementation constraints contribute method demand without importing the foreign owner's frame");
        StorageDemandSelfTest();
        DeclarationValueDemandSelfTest();
        MetadataSelfTest();
        Console.WriteLine("[nullable representation frame] self-test OK (source correspondence, scopes, declaration/body demand, fixed point)");
    }

    static void DeclarationValueDemandSelfTest()
    {
        var root = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"ValueDemand","methods":[
          {"name":"make","typeParams":[{"name":"T"}],"params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"fqn","name":"ValueOwner","args":[{"t":"tv","scope":"method","i":0}]},"body":[]},
          {"name":"identity","typeParams":[{"name":"T"}],"params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"tv","scope":"method","i":0},"body":[]}],
         "types":[
          {"kind":"class","name":"ValueOwner","typeParams":[{"name":"T"}],"fields":[
            {"name":"value","vis":"private","type":{"t":"tv","scope":"type","i":0}}]},
          {"kind":"class","name":"ForeignOwner","typeParams":[{"name":"T"}],"fields":[
            {"name":"value","type":{"t":"tv","scope":"type","i":0}}]}]}
        """));
        var policy = GenericRepresentationPolicy.CreateSource(new[] { root }, new Dictionary<string, string> {
            ["ForeignOwner"] = "NativeOwner",
        }, null);
        var dispatch = (BirFunctionDeclaration)root.Methods.Value[1].DeepClone();
        dispatch.Name = "dispatch";
        dispatch.Abstract = true;
        dispatch.Virtual = true;
        var nativeDispatch = (BirFunctionDeclaration)dispatch.DeepClone();
        nativeDispatch.Name = "nativeDispatch";
        nativeDispatch.Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("ForeignOwner"), Member = "identity" } };
        var nativeIntrinsic = (BirFunctionDeclaration)dispatch.DeepClone();
        nativeIntrinsic.Name = "nativeIntrinsic";
        nativeIntrinsic.Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrIntrinsic") } };
        root.Methods = root.Methods.Value.Concat(new[] { dispatch, nativeDispatch, nativeIntrinsic }).ToArray();
        var original = root.DeepClone();
        var demands = Collect(new[] { root }, policy: policy);
        var value = demands.Single(owner => owner.View.Name == "ValueOwner");
        var foreign = demands.Single(owner => owner.View.Name == "ForeignOwner");
        var methods = demands.Single(owner => ReferenceEquals(owner.Declaration, root)).Methods;
        if (!value.Frame.StorageIndices.SequenceEqual(new[] { 0 })
            || !methods.Single(method => method.View.Name == "make").Frame.StorageIndices.SequenceEqual(new[] { 0 })
            || !methods.Single(method => method.View.Name == "identity").Frame.StorageIndices.SequenceEqual(new[] { 0 })
            || !methods.Single(method => method.View.Name == "dispatch").Frame.StorageIndices.SequenceEqual(new[] { 0 })
            || methods.Single(method => method.View.Name == "nativeDispatch").Frame.PhysicalArity != 1
            || methods.Single(method => method.View.Name == "nativeIntrinsic").Frame.PhysicalArity != 1
            || foreign.Frame.PhysicalArity != 1 || !root.StructurallyEquals(original))
            throw new InvalidOperationException("Declaration value demand changed an unrelated or native frame");
        Console.WriteLine("[declaration value demand] self-test OK (stored variable, factory fixed point, method value slots and native owner frames)");
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
        var root = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"StorageCalls","methods":[
          {"name":"virtualUse","declarationId":"virtual-use","virtual":true,"typeParams":[{"name":"T"}],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","declarationId":"forward","typeArgs":[{"t":"tv","scope":"method","i":0}]}}]},
          {"name":"forward","declarationId":"forward","typeParams":[{"name":"T"}],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","declarationId":"leaf","typeArgs":[{"t":"tv","scope":"method","i":0}]}}]},
          {"name":"leaf","declarationId":"leaf","typeParams":[{"name":"T"}],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","declarationId":"storage-source","typeArgs":[{"t":"tv","scope":"method","i":0}]}}]},
          {"name":"nullableForward","typeParams":[{"name":"T"}],"params":[],
           "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"exprStmt","expr":{"k":"callStatic","declarationId":"storage-source","typeArgs":[
              {"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]}}]}],
         "types":[
          {"kind":"class","name":"StorageOuter","typeParams":[{"name":"T"}],"fields":[
            {"name":"value","type":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"type","i":0}]}}]},
          {"kind":"class","name":"StorageNested","typeParams":[{"name":"U"},{"name":"T"}],
           "semanticOwner":"StorageOuter","outerTypeParamCount":1,"outerTypeParamOffset":1},
          {"kind":"class","name":"StorageIndirect","typeParams":[{"name":"T"}],"fields":[
            {"name":"value","type":{"t":"fqn","name":"StorageOuter","args":[{"t":"tv","scope":"type","i":0}]}}]},
          {"kind":"class","name":"StorageScope","typeParams":[{"name":"T"}],"methods":[
            {"name":"both","typeParams":[{"name":"T"}],"params":[{"name":"owner","type":
              {"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"type","i":0}]}}],
             "ret":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"method","i":0}]},"body":[]}]}]}
        """));
        var original = root.DeepClone();
        var demands = Collect(new[] { root }, types, methods);
        var calls = demands.Single(owner => ReferenceEquals(owner.Declaration, root)).Methods;
        foreach (var name in new[] { "leaf", "forward" })
        {
            var method = calls.Single(method => method.View.Name == name);
            Check(method.Frame.StorageIndices.SequenceEqual(new[] { 0 })
                && method.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 }) && method.Frame.PhysicalArity == 3,
                "body-only role demand reaches nonvirtual callers: " + name);
        }
        var virtualUse = calls.Single(method => method.View.Name == "virtualUse");
        Check(virtualUse.Frame.PhysicalArity == 1
            && virtualUse.Body.For("method", NullableRepresentationFrame.Role.Storage).SetEquals(new[] { 0 })
            && virtualUse.Body.For("method", NullableRepresentationFrame.Role.NullableStorage).SetEquals(new[] { 0 }),
            "virtual body storage demand does not change its dispatch ABI");
        var nullableUse = calls.Single(method => method.View.Name == "nullableForward");
        Check(nullableUse.Frame.StorageIndices.Count == 0
            && nullableUse.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 })
            && nullableUse.Frame.NullableIndices.SequenceEqual(new[] { 0 }),
            "storage of a nullable argument demands nullable-storage alongside its ordinary nullable form");
        var indirect = demands.Single(owner => owner.View.Name == "StorageIndirect");
        Check(indirect.Frame.StorageIndices.SequenceEqual(new[] { 0 })
            && indirect.Frame.NullableStorageIndices.SequenceEqual(new[] { 0 }), "type application fixed point retains roles");
        var nested = demands.Single(owner => owner.View.Name == "StorageNested");
        Check(nested.Frame.PhysicalOrder.SequenceEqual(new[] { 1, 2, 3, 0 })
            && nested.Frame.StorageIndices.SequenceEqual(new[] { 1 })
            && nested.Frame.NullableStorageIndices.SequenceEqual(new[] { 1 }), "enclosing captures retain storage role groups");
        var scoped = demands.Single(owner => owner.View.Name == "StorageScope");
        Check(scoped.Frame.PhysicalArity == 3 && scoped.Methods.Single().Frame.PhysicalArity == 3,
            "method and owner source index zero remain independent");
        Check(root.StructurallyEquals(original), "role analysis does not rewrite declarations");
        var localRoot = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"LocalStorage","methods":[{"name":"entry","typeParams":[{"name":"U"}],"params":[],
          "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
            {"k":"localFun","id":"local-storage","decl":{"name":"local","typeParams":[{"name":"T"}],
              "_syntheticTypeArgs":[{"t":"tv","scope":"method","i":0}],"params":[],
              "ret":{"t":"fqn","name":"StorageSource","args":[{"t":"tv","scope":"method","i":0}]},"body":[]}},
            {"k":"exprStmt","expr":{"k":"callLocal","id":"local-storage","typeArgs":[{"t":"tv","scope":"method","i":0}],"args":[]}}]}]}
        """));
        var locals = Collect(new[] { localRoot }, types, methods).Single().Methods;
        Check(locals.Single(method => method.View.Name == "entry").Frame.PhysicalArity == 3,
            "local call explicitly relates its storage demand to the lexical method frame");
        localRoot.Methods.Value[0].Body = localRoot.Methods.Value[0].Body.Value.Take(1).ToArray();
        locals = Collect(new[] { localRoot }, types, methods).Single().Methods;
        Check(locals.Single(method => method.View.Name == "entry").Frame.PhysicalArity == 1
            && locals.Single(method => method.IsLocal).Frame.PhysicalArity == 3,
            "a local function's own method index is not an implicit lexical capture");
    }

    static void MetadataSelfTest()
    {
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var owner = new JsonObject {
            ["kind"] = "class", ["name"] = "FrameOwner", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }, new JsonObject { ["name"] = "N" }),
        };
        KotlinSupertypesRecord.Merge(owner, new JsonObject { [NullableRepresentationFrame.MetadataKey] = frame.ToJson() });
        KotlinSupertypesRecord.Merge(owner, new JsonObject {
            ["bounds"] = new JsonObject { ["0"] = new JsonArray(TypeJson.Fqn("System.Object")) },
        });
        var sourceResult = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("method", 0)) });
        var method = new JsonObject {
            ["name"] = "physical", [DeclarationIdentityBinding.Key] = "frame-test-method", ["declarationSourceName"] = "source",
            ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }, new JsonObject { ["name"] = "N" }), ["params"] = new JsonArray(),
            ["ret"] = TypeJson.Write(new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 1) })),
            [DeclarationIdentityBinding.SemanticSignatureKey] = new JsonObject {
                ["params"] = new JsonArray(TypeJson.Fqn("kotlin.Int")), ["ret"] = TypeJson.Fqn("kotlin.Unit"),
            },
            [NullableRepresentationTypes.MethodFrameKey] = frame.ToJson().ToJsonString(),
        };
        var root = new JsonObject { ["fileClass"] = "FrameFile", ["types"] = new JsonArray(owner), ["methods"] = new JsonArray(method) };
        var runtime = root.DeepClone();
        var signatures = new SourceDeclarationSignatures();
        signatures.Capture(new[] { new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            DeclarationId = "frame-test-method", Params = Array.Empty<BirParameter>(), Ret = sourceResult,
        } } } });
        DeclarationIdentityBinding.ApplyLocal(new[] { root },
            new Dictionary<string, string> { ["frame-test-method"] = "physical" }, new HashSet<string>(),
            signatures, new Dictionary<string, int[]>(), null);
        RoundtripMetadata.PrepareMetadata(root, runtimeMode: false);
        var stamped = BirGraphDom.ReadBirFileDeclaration(root);
        RoundtripMetadata.CompleteMetadata(stamped, runtimeMode: false);
        owner = ((BirGraphValue)stamped.Types.Value[0]).ToDom();
        method = stamped.Methods.Value[0].ToDom();

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
            || methodPayload["signature"]["params"] is not JsonArray { Count: 0 }
            || owner[KotlinSupertypesRecord.PreKey] != null || method[NullableRepresentationTypes.MethodFrameKey] != null)
            throw new InvalidOperationException("Nullable representation metadata lost source facts or leaked transient frames");
        RoundtripMetadata.PrepareMetadata(runtime, runtimeMode: true);
        var stripped = BirGraphDom.ReadBirFileDeclaration(runtime);
        RoundtripMetadata.CompleteMetadata(stripped, runtimeMode: true);
        var runtimeOwner = (BirClassDefinition)stripped.Types.Value[0];
        var runtimeMethod = stripped.Methods.Value[0];
        if (runtimeOwner.KotlinSupertypesPre.IsPresent || runtimeOwner.RoundtripSupertypesPre.IsPresent
            || runtimeOwner.RoundtripTypeHeaderPending.IsPresent || runtimeMethod.NullableRepresentationFrame.IsPresent
            || runtimeMethod.RoundtripDeclarationIdentity.IsPresent || runtimeMethod.DeclarationId.IsPresent
            || runtimeMethod.DeclarationSourceName.IsPresent || runtimeMethod.RoundtripMetadataPending.IsPresent)
            throw new InvalidOperationException("Runtime metadata stripping leaked nullable representation facts");
    }
}
