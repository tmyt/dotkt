using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void CallDescriptorSelfTest()
    {
        var shape = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0)),
            TypeJson.Fqn("dotkt$CharSequence"));
        var arguments = new JsonArray(
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.String"), ["value"] = "first" },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.String"), ["value"] = "second" });
        var call = new JsonObject {
            ["k"] = "callStatic", ["method"] = "ordinaryGeneric", ["shapeTypes"] = shape.DeepClone(),
            ["typeArgs"] = new JsonArray(TypeJson.Fqn("System.String")), ["args"] = arguments,
        };
        var reference = new JsonObject {
            ["k"] = "callInstance", ["method"] = "replace",
            ["shapeTypes"] = new JsonArray(TypeJson.Write(new TypeNode.ByRef(new TypeNode.Tv("type", 0)))),
            ["argTypes"] = new JsonArray(TypeJson.Write(new TypeNode.ByRef(new TypeNode.Fqn("System.String")))),
            ["args"] = new JsonArray(new JsonObject { ["k"] = "local", ["name"] = "address" }),
        };
        var referenceShape = reference["shapeTypes"].DeepClone();
        var root = new JsonObject { ["body"] = new JsonArray(call, reference) };
        EnsurePlainCallDescriptors(root);
        if (!JsonNode.DeepEquals(call["sig"], shape) || call.ContainsKey("shapeTypes")
            || !ReferenceEquals(call["args"], arguments)
            || !JsonNode.DeepEquals(reference["sig"], referenceShape) || reference.ContainsKey("shapeTypes"))
            throw new InvalidOperationException("Call normalization lost the selected open signature or exact address slot");
        var signature = call["sig"].DeepClone();
        var late = new JsonObject { ["k"] = "callStatic", ["method"] = "late", ["args"] = new JsonArray() };
        ((JsonArray)root["body"]).Add(late);
        EnsurePlainCallDescriptors(root);
        if (!JsonNode.DeepEquals(call["sig"], signature) || late["sig"] is not JsonArray { Count: 0 })
            throw new InvalidOperationException("Late call normalization changed an existing descriptor or skipped synthesis");
        Console.WriteLine("[call descriptors] self-test OK (open generic, representation slot, exact byref, late synthesis)");
    }
}
