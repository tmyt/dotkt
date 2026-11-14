using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static class InnerRepresentationFrameTests
{
    public static void SelfTest()
    {
        StaticNestedOwnership();
        ApplicationCorrespondence();
        DeclarationArgumentOrder();
        SourceCarrierIdentity();
        SyntheticOwnerFrames();
        // Declarations use enclosing-first variables; Kotlin inner applications use own-first arguments.
        var source = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"fileClass":"InnerFrameProbe","types":[
          {"kind":"class","name":"Box","typeParams":[{"name":"T"}]},
          {"kind":"class","name":"Outer","typeParams":[{"name":"O"}],
           "fields":[{"name":"outer","type":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}]},
          {"kind":"class","name":"Outer.Inner","semanticOwner":"Outer","mods":{"inner":true},
           "outerTypeParamCount":1,"typeParams":[{"name":"O"},{"name":"I"}],
           "base":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":1}}]},
           "fields":[{"name":"captured","type":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}],
           "methods":[{"name":"read","params":[],"ret":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},
             "body":[{"k":"return","value":{"k":"field","name":"captured","recv":{"k":"this"},
               "ownerType":{"t":"fqn","name":"Outer.Inner","args":[{"t":"tv","scope":"type","i":1},{"t":"tv","scope":"type","i":0}]},
               "memberOwnerTypeParams":[{"name":"O"},{"name":"I"}],"memberType":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}}]}]}]}
        """));
        NullableRepresentationMaterialization.Apply(new[] { source }, _ => false);
        var materializedOwner = TypeJson.Write(((BirFieldGet)((BirReturn)source.Types.Value[2].Methods.Value[0].Body.Value[0]).Value.Value).OwnerType.Value).ToJsonString();
        TypeOwnershipLowering.PrepareOwnershipFacts(new[] { source });
        TypeOwnershipLowering.ProjectInnerApplications(new[] { source }, null);
        var owner = ((BirFieldGet)((BirReturn)source.Types.Value[2].Methods.Value[0].Body.Value[0]).Value.Value).OwnerType.Value;
        var arity = source.Types.Value[2].TypeParams.Value.Length;
        var expected = new TypeNode.Fqn("Outer.Inner", Enumerable.Range(0, arity)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        if (owner != expected)
            throw new InvalidOperationException($"Inner self field lost physical frame: {TypeJson.Write(owner)}; expected {TypeJson.Write(expected)}; after materialization: {materializedOwner}");
    }

    static void StaticNestedOwnership()
    {
        var root = BirGraphDom.ReadBirFileDeclaration(JsonNode.Parse("""
        {"types":[
          {"kind":"class","name":"Owner","typeParams":[{"name":"T"}]},
          {"kind":"class","name":"Owner.Nested","semanticOwner":"Owner","typeParams":[{"name":"U"}]},
          {"kind":"class","name":"Owner.Inner","semanticOwner":"Owner","mods":{"inner":true},"typeParams":[{"name":"T"},{"name":"V"}],"outerTypeParamCount":1},
          {"kind":"class","name":"Owner.Helper","semanticOwner":"Owner","generated":true,"typeParams":[{"name":"T"}]},
          {"kind":"class","name":"Plain"},
          {"kind":"class","name":"Plain.Nested","semanticOwner":"Plain"}]}
        """));
        var original = root.Types.Value;
        GenericStaticOwnerBinding.Materialize(new[] { root });
        var types = root.Types.Value;
        var carrier = types.Single(type => type.StaticCarrier.HasValue);
        if (!original.Select((type, index) => ReferenceEquals(type, types[index])).All(value => value)
            || types[1].NestedIn.OrDefault() != carrier.Name.Value
            || types[1].SemanticOwner.OrDefault() != "Owner"
            || types[1].TypeParams.Value.Length != 1
            || types[2].SemanticOwner.OrDefault() != "Owner"
            || types[3].SemanticOwner.OrDefault() != "Owner"
            || types[5].SemanticOwner.OrDefault() != "Plain"
            || TypeParameterFrame.Count(carrier) != 0)
            throw new InvalidOperationException("Static nesting changed Kotlin frames or captured implementation ownership");
        var constructedOwner = TypeJson.Write(new TypeNode.Fqn("Owner", new TypeNode[] { new TypeNode.Tv("type", 0) }));
        var projected = root.ToDom();
        var helperCall = new JsonObject { ["k"] = "callStatic", ["owner"] = constructedOwner.DeepClone() };
        projected["methods"] = new JsonArray(new JsonObject { ["name"] = "probe", ["body"] = new JsonArray(
            new JsonObject { ["k"] = "exprStmt", ["expr"] = helperCall }) });
        // One terminal projection exercises the still-live downstream physical binder.
        GenericStaticOwnerBinding.ApplyAll(new[] { projected }, null);
        if (!JsonNode.DeepEquals(helperCall["owner"], constructedOwner))
            throw new InvalidOperationException("Static nesting retargeted a constructed generic method owner");
        GenericStaticOwnerBinding.SelfTestPlacement();
    }

    static void SyntheticOwnerFrames()
    {
        foreach (var scenario in new[] { "lexical", "shared", "splitRoles", "permuted" })
        {
            var ownerFrame = new NullableRepresentationFrame(1, Array.Empty<int>(),
                physicalOrder: scenario == "permuted" ? new[] { 1, 0 } : null,
                storageIndices: new[] { 0 });
            var captureFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
            var owner = new BirClassDefinition { Name = "CaptureOwner", Kind = "class",
                TypeParams = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "P" } },
                KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(ownerFrame) };
            var synthetic = new BirClosureDefinition { Name = "CaptureClosure", SemanticOwner = "CaptureOwner",
                TypeParams = new[] { new BirTypeParameter { Name = "M" }, new BirTypeParameter { Name = "PM" } },
                KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(captureFrame) };
            TypeNode[] args = scenario switch {
                "shared" => new[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1) },
                "splitRoles" => new[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("method", 1) },
                "permuted" => new[] { new TypeNode.Tv("type", 1), new TypeNode.Tv("type", 0) },
                _ => new[] { new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1) },
            };
            var call = new BirNewClosure { SynthClass = synthetic, TypeArgs = args };
            var root = new BirFileDeclaration { FileClass = "CaptureFile", Types = new IBirTypeDeclaration[] { owner },
                Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } } } } };
            TypeOwnershipLowering.PrepareOwnershipFacts(new[] { root });
            var frame = synthetic.KotlinSupertypesPre.Value.NullableFrame;
            var shared = scenario is "shared" or "permuted";
            var order = shared ? ownerFrame.PhysicalOrder.ToArray() : new[] { 0, 2, 1, 3 };
            if (frame.SourceArity != (shared ? 1 : 2) || !frame.PhysicalOrder.SequenceEqual(order)
                || frame.PhysicalArity != synthetic.TypeParams.Value.Length
                || frame.PhysicalArity != call.TypeArgs.Value.Length)
                throw new InvalidOperationException($"Synthetic owner frame lost exact source roles: {scenario}");
            var stable = root.DeepClone();
            TypeOwnershipLowering.PrepareOwnershipFacts(new[] { root });
            if (!root.StructurallyEquals(stable))
                throw new InvalidOperationException($"Synthetic owner preparation expanded an already-complete frame: {scenario}");
        }
        TypeOwnershipLowering.SelfTestSourcePreparation();
    }

    static void DeclarationArgumentOrder()
    {
        BirTypeParameter[] Parameters(params string[] names) => names.Select(name => new BirTypeParameter { Name = name }).ToArray();
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Kind = "class", Name = "Outer", TypeParams = Parameters("O", "NO") },
            new BirClassDefinition { Kind = "class", Name = "Outer.Inner", Mods = new BirModifiers { Inner = true },
                OuterTypeParamCount = 2, SemanticOwner = "Outer", TypeParams = Parameters("O", "NO", "I", "NI") },
            new BirClassDefinition { Kind = "class", Name = "Outer.Inner.Leaf", Mods = new BirModifiers { Inner = true },
                OuterTypeParamCount = 4, SemanticOwner = "Outer.Inner", TypeParams = Parameters("O", "NO", "I", "NI", "L", "NL") },
        } };
        TypeNode Arg(string name) => new TypeNode.Fqn(name);
        var o = Arg("O"); var no = Arg("NO"); var i = Arg("I"); var ni = Arg("NI");
        var nested = new TypeNode.Fqn("Outer.Inner", new[] { i, ni, o, no });
        var leaf = new TypeNode.Fqn("Outer.Inner.Leaf", new TypeNode[] { nested, Arg("NL"), i, ni, o, no });
        var order = new InnerApplicationOrder(new[] { root }, null);
        var declaration = order.DeclarationArguments(leaf);
        if (!declaration.SequenceEqual(new TypeNode[] { o, no, i, ni, nested, Arg("NL") }))
            throw new InvalidOperationException("Declaration substitution changed argument payloads or lost enclosing order");
        var field = new BirFieldDeclaration { Name = "value", Type = leaf };
        root.Fields = new[] { field };
        TypeOwnershipLowering.ProjectInnerApplications(new[] { root }, null);
        var projectedNested = new TypeNode.Fqn("Outer.Inner", new[] { o, no, i, ni });
        var expected = new TypeNode.Fqn("Outer.Inner.Leaf", new TypeNode[] { o, no, i, ni, projectedNested, Arg("NL") });
        if (field.Type.Value != expected)
            throw new InvalidOperationException("Nested argument applications were projected more than once");
        var enclosingNested = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { i, ni, nested, no });
        field.Type = enclosingNested;
        TypeOwnershipLowering.ProjectInnerApplications(new[] { root }, null);
        var expectedEnclosing = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { projectedNested, no, i, ni });
        if (field.Type.Value != expectedEnclosing)
            throw new InvalidOperationException("An enclosing argument payload was projected more than once");
        Console.WriteLine("[inner declaration argument order] self-test OK");
    }

    static void ApplicationCorrespondence()
    {
        // Declaration [O,I,N(I)] versus the intermediate Kotlin application [I,N(I),O].
        var declaration = new NullableRepresentationFrame(2, new[] { 1 });
        var application = declaration.ForApplication(new[] { 1, 0 }, new[] { 1, 2, 0 });
        var outer = new TypeNode.Fqn("OuterArgument");
        var inner = new TypeNode.Fqn("InnerArgument");
        var actual = application.Close(new TypeNode[] { inner, outer }, type => type,
            type => new TypeNode.Nullable(type));
        if (!actual.SequenceEqual(new TypeNode[] { inner, new TypeNode.Nullable(inner), outer })
            || !application.NullableIndices.SequenceEqual(new[] { 0 }))
            throw new InvalidOperationException("Application frame lost nullable companion source identity");

        // Enclosing nullable/storage companions are separate roles, not additional source parameters.
        var roles = new NullableRepresentationFrame(2, new[] { 0, 1 }, new[] { 0, 2, 4, 1, 3, 5 },
            storageIndices: new[] { 0 }, nullableStorageIndices: new[] { 1 });
        var reordered = roles.ForApplication(new[] { 1, 0 }, new[] { 3, 4, 5, 0, 1, 2 });
        var closed = reordered.Close(new TypeNode[] { inner, outer }, type => type,
            type => new TypeNode.Nullable(type), type => new TypeNode.Array(type),
            type => new TypeNode.Array(new TypeNode.Nullable(type)));
        if (!closed.SequenceEqual(new TypeNode[] { inner, new TypeNode.Nullable(inner),
            new TypeNode.Array(new TypeNode.Nullable(inner)), outer, new TypeNode.Nullable(outer), new TypeNode.Array(outer) }))
            throw new InvalidOperationException("Application frame mixed enclosing and own representation roles");
        Console.WriteLine("[inner application correspondence] self-test OK (source identity and independent physical order)");
    }

    static void SourceCarrierIdentity()
    {
        var source = new TypeNode.Fqn("Sample.Outer.Inner", new TypeNode[] {
            new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0),
        });
        var root = JsonNode.Parse("""
        {"fileClass":"Sample.File","types":[
          {"name":"Sample.Outer","typeParams":[{"name":"O"}]},
          {"name":"Sample.Outer.Inner","nestedIn":"Sample.Outer","typeParams":[{"name":"I"},{"name":"N"}]}],
          "fields":[{"name":"slot"}]}
        """)!;
        root["fields"]![0]!["nullableGeneric"] = TypeNode.ToJson(source);
        var outer = new TypeNode.Fqn("Sample.Outer", new TypeNode[] { new TypeNode.Tv("method", 0) });
        var signatures = new SourceDeclarationSignatures();
        signatures.Capture(new[] { new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            DeclarationId = "declaration", Params = new[] { new BirParameter { Type = source }, new BirParameter { Type = outer } },
            Ret = new TypeNode.Fqn("kotlin.Unit"),
        } } } });
        var suspendShape = new TypeNode.Fn(true, source, new TypeNode[] { source, outer });
        var inlineBody = new BirInlinePayload {
            V = 1, Fqn = "Sample.File.inline", Owner = "Sample.File", FileClass = "Sample.File", Recv = "none", Static = true,
            TypeParams = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "I" } },
            Params = new[] { new BirParameter { Name = "input", Type = source } }, Ret = source,
            Body = new IBirStatement[] { new BirVariable { Name = "inner", Type = source }, new BirVariable { Name = "outer", Type = outer } },
            Lifted = Array.Empty<BirFunctionDeclaration>(),
        };
        root["inlineBir"] = Convert.ToBase64String(ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, inlineBody));
        root["suspendResult"] = TypeNode.ToJson(suspendShape);
        var defaultBody = new BirValueBlock {
            Type = source,
            Stmts = new IBirStatement[] { new BirVariable { Name = "outer", Type = outer } },
            Result = new BirClassReference { Type = source },
        };
        var defaultAttribute = DefaultBirCarrier.Create(0, defaultBody).ToDom();
        root["attrs"] = new JsonArray(defaultAttribute);
        OpaqueCarrierTypeBinding.ApplyAll(new[] { root }, ReferenceMetadataIndex.Build(Array.Empty<string>()),
            signatures);
        signatures.TryGetValue("declaration", out var signature);
        var expected = new TypeNode.Fqn("Sample.Outer`1+Inner`2", source.Args);
        var slot = TypeJson.Read(JsonNode.Parse(root["fields"]![0]!["nullableGeneric"]!.GetValue<string>()));
        if (slot != expected || signature.Parameters[0] != expected)
            throw new InvalidOperationException("Source carriers lost nested identity or changed source argument order");
        if (signature.Parameters[1] != outer)
            throw new InvalidOperationException("Source carriers unnecessarily changed a top-level classifier");
        var inline = ExecutableBirCodec.ReadInline(ExecutableBirCodec.Codec,
            Convert.FromBase64String(root["inlineBir"]!.GetValue<string>()));
        var suspendPayload = (TypeNode.Fn)TypeJson.Read(JsonNode.Parse(root["suspendResult"]!.GetValue<string>()));
        if (((BirVariable)inline.Body.Value[0]).Type.Value != expected || ((BirVariable)inline.Body.Value[1]).Type.Value != outer
            || suspendPayload.Ret != expected || suspendPayload.Params[0] != expected || suspendPayload.Params[1] != outer
            || !suspendPayload.Suspend)
            throw new InvalidOperationException("Executable carriers must bind nested identities without retargeting their top-level owners");
        var defaultPayload = (BirValueBlock)DefaultBirCarrier.DecodeTyped(defaultAttribute);
        if (defaultPayload.Type.Value != expected || ((BirClassReference)defaultPayload.Result.Value).Type.Value != expected
            || ((BirVariable)defaultPayload.Stmts.Value[0]).Type.Value != outer)
            throw new InvalidOperationException("Typed default carriers must bind nested identities without retargeting top-level owners");
        if (inline.Params.Value[0].Type.Value != source)
            throw new InvalidOperationException("Inline overload signatures must remain semantic");
    }
}
