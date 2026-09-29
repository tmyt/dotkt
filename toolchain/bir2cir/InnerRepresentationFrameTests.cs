using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class InnerRepresentationFrameTests
{
    public static void SelfTest()
    {
        ApplicationCorrespondence();
        DeclarationArgumentOrder();
        SourceCarrierIdentity();
        // Declarations use enclosing-first variables; Kotlin inner applications use own-first arguments.
        var root = JsonNode.Parse("""
        {"fileClass":"InnerFrameProbe","types":[
          {"kind":"class","name":"Box","typeParams":["T"]},
          {"kind":"class","name":"Outer","typeParams":["O"],
           "fields":[{"name":"outer","type":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}]},
          {"kind":"class","name":"Outer.Inner","semanticOwner":"Outer","mods":{"inner":true},
           "outerTypeParamCount":1,"typeParams":["O","I"],
           "base":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":1}}]},
           "fields":[{"name":"captured","type":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}],
           "methods":[{"name":"read","params":[],"ret":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},
             "body":[{"k":"return","value":{"k":"field","name":"captured","recv":{"k":"this"},
               "ownerType":{"t":"fqn","name":"Outer.Inner","args":[{"t":"tv","scope":"type","i":1},{"t":"tv","scope":"type","i":0}]},
               "memberOwnerTypeParams":["O","I"],"memberType":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}}}]}]}]}
        """)!;
        NullableRepresentationMaterialization.Apply(new[] { root }, _ => false);
        var materializedOwner = root["types"]![2]!["methods"]![0]!["body"]![0]!["value"]!["ownerType"]!.ToJsonString();
        TypeOwnershipLowering.PrepareOwnershipFacts(new[] { root });
        TypeOwnershipLowering.ProjectInnerApplications(new[] { root }, null);
        var owner = TypeJson.Read(root["types"]![2]!["methods"]![0]!["body"]![0]!["value"]!["ownerType"]);
        var arity = ((JsonArray)root["types"]![2]!["typeParams"]!).Count;
        var expected = new TypeNode.Fqn("Outer.Inner", Enumerable.Range(0, arity)
            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        if (owner != expected)
            throw new InvalidOperationException($"Inner self field lost physical frame: {TypeJson.Write(owner)}; expected {TypeJson.Write(expected)}; after materialization: {materializedOwner}");
    }

    static void DeclarationArgumentOrder()
    {
        var root = JsonNode.Parse("""
        {"types":[
          {"name":"Outer","typeParams":["O","NO"]},
          {"name":"Outer.Inner","mods":{"inner":true},"outerTypeParamCount":2,"semanticOwner":"Outer"},
          {"name":"Outer.Inner.Leaf","mods":{"inner":true},"outerTypeParamCount":4,"semanticOwner":"Outer.Inner"}],
         "fields":[]}
        """)!;
        TypeNode Arg(string name) => new TypeNode.Fqn(name);
        var o = Arg("O"); var no = Arg("NO"); var i = Arg("I"); var ni = Arg("NI");
        var nested = new TypeNode.Fqn("Outer.Inner", new[] { i, ni, o, no });
        var leaf = new TypeNode.Fqn("Outer.Inner.Leaf", new TypeNode[] { nested, Arg("NL"), i, ni, o, no });
        var order = new InnerApplicationOrder(new[] { root }, null);
        var declaration = order.DeclarationArguments(leaf);
        if (!declaration.SequenceEqual(new TypeNode[] { o, no, i, ni, nested, Arg("NL") }))
            throw new InvalidOperationException("Declaration substitution changed argument payloads or lost enclosing order");
        ((JsonArray)root["fields"]!).Add(new JsonObject { ["name"] = "value", ["type"] = TypeJson.Write(leaf) });
        TypeOwnershipLowering.ProjectInnerApplications(new[] { root }, null);
        var projectedNested = new TypeNode.Fqn("Outer.Inner", new[] { o, no, i, ni });
        var expected = new TypeNode.Fqn("Outer.Inner.Leaf", new TypeNode[] { o, no, i, ni, projectedNested, Arg("NL") });
        if (TypeJson.Read(root["fields"]![0]!["type"]) != expected)
            throw new InvalidOperationException("Nested argument applications were projected more than once");
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
          {"name":"Sample.Outer","typeParams":["O"]},
          {"name":"Sample.Outer.Inner","nestedIn":"Sample.Outer","typeParams":["I","N"]}],
          "fields":[{"name":"slot"}]}
        """)!;
        root["fields"]![0]!["nullableGeneric"] = TypeNode.ToJson(source);
        var outer = new TypeNode.Fqn("Sample.Outer", new TypeNode[] { new TypeNode.Tv("method", 0) });
        var signature = new JsonObject { ["params"] = new JsonArray(TypeJson.Write(source), TypeJson.Write(outer)) };
        OpaqueCarrierTypeBinding.ApplyAll(new[] { root }, ReferenceMetadataIndex.Build(Array.Empty<string>()),
            new Dictionary<string, JsonObject> { ["declaration"] = signature });
        var expected = new TypeNode.Fqn("Sample.Outer`1+Inner`2", source.Args);
        var slot = TypeJson.Read(JsonNode.Parse(root["fields"]![0]!["nullableGeneric"]!.GetValue<string>()));
        if (slot != expected || TypeJson.Read(signature["params"]![0]) != expected)
            throw new InvalidOperationException("Source carriers lost nested identity or changed source argument order");
        if (TypeJson.Read(signature["params"]![1]) != outer)
            throw new InvalidOperationException("Source carriers unnecessarily changed a top-level classifier");
    }
}
