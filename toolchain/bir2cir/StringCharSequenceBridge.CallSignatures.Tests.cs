using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class StringCharSequenceBridge
{
    internal static void CallSignatureSelfTest()
    {
        var shape = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0)),
            TypeJson.Fqn("dotkt$CharSequence"));
        var arguments = new JsonArray(
            new JsonObject { ["k"] = "local", ["name"] = "value" },
            new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.String"), ["value"] = "text" },
            new JsonObject { ["k"] = "local", ["name"] = "witness" });
        var call = new JsonObject {
            ["k"] = "callInstance", ["method"] = "ordinaryGeneric", ["shapeTypes"] = shape.DeepClone(),
            ["ownerType"] = TypeJson.Write(new TypeNode.Fqn("Owner", new TypeNode[] { new TypeNode.Tv("type", 5) })),
            ["typeArgs"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 4))), ["args"] = arguments,
            [DeclarationIdentityBinding.Key] = "selected-declaration",
        };
        var physical = new JsonObject {
            ["k"] = "callStatic", ["method"] = "physical",
            ["sig"] = new JsonArray(TypeJson.Fqn("System.String")),
            ["shapeTypes"] = new JsonArray(TypeJson.Fqn("dotkt$CharSequence")),
            ["args"] = new JsonArray(arguments[1].DeepClone()),
        };
        var original = call.DeepClone();
        var physicalOriginal = physical.DeepClone();
        var root = Apply(new JsonObject { ["body"] = new JsonArray(call, physical) }, null, null, out _);
        var adapted = (JsonObject)root["body"][0];
        var adaptedArguments = adapted["args"] as JsonArray;
        if (adapted.ContainsKey("sig") || !JsonNode.DeepEquals(adapted["shapeTypes"], shape)
            || !JsonNode.DeepEquals(adapted["ownerType"], original["ownerType"])
            || !JsonNode.DeepEquals(adapted["typeArgs"], original["typeArgs"])
            || !JsonNode.DeepEquals(adapted[DeclarationIdentityBinding.Key], original[DeclarationIdentityBinding.Key])
            || adaptedArguments is not { Count: 3 }
            || !JsonNode.DeepEquals(adaptedArguments[0], original["args"][0])
            || !JsonNode.DeepEquals(adaptedArguments[2], original["args"][2])
            || Str(adaptedArguments[1]?["k"]) != "new"
            || TypeJson.Read(adaptedArguments[1]?["type"]) is not TypeNode.Fqn { Name: Adapter }
            || !JsonNode.DeepEquals(adaptedArguments[1]?["args"]?[0], original["args"][1])
            || !JsonNode.DeepEquals(root["body"][1], physicalOriginal))
            throw new InvalidOperationException("Call bridge changed source arity, witnesses, selected identity, or generic frames");
        Console.WriteLine("[call representation] self-test OK (source shape, physical precedence, witness, identity, generic frames)");
    }
}
