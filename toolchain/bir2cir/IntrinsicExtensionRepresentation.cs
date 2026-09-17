using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// At a trusted intrinsic boundary, the selected CLR declaration owns its signature. Ordinary Kotlin
// values may cross into storage companions, and returned storage values cross back to ordinary slots.
static class IntrinsicExtensionRepresentation
{
    internal static JsonObject Adapt(JsonObject call, TypeNode[] parameters, TypeNode result)
    {
        var arguments = (JsonArray)call["args"];
        var original = ((JsonArray)call["argTypes"]).Select(TypeJson.Read).ToArray();
        if (arguments.Count != parameters.Length || original.Length != parameters.Length)
            throw new InvalidOperationException("Intrinsic extension parameter vector does not match its CLR declaration");
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i] != original[i])
            {
                if (parameters[i] is TypeNode.ByRef || original[i] is TypeNode.ByRef)
                    throw new InvalidOperationException("Intrinsic extension cannot convert a managed-reference representation");
                arguments[i] = Cast(parameters[i], arguments[i]?.DeepClone());
            }
        call["argTypes"] = new JsonArray(parameters.Select(TypeJson.Write).ToArray());
        var logicalResult = TypeJson.Read(call["ret"]);
        call["ret"] = TypeJson.Write(result);
        if (call["sty"] != null) call["sty"] = TypeJson.Write(result);
        return logicalResult != null && logicalResult != result
            && result is not TypeNode.Fqn { Name: "System.Void" or "void" }
            ? Cast(logicalResult, call) : call;
    }

    static JsonObject Cast(TypeNode type, JsonNode value) => new() {
        ["k"] = "cast", ["type"] = TypeJson.Write(type), ["e"] = value,
    };

    internal static void SelfTest()
    {
        var ordinary = new TypeNode.Tv("method", 2);
        var storage = new TypeNode.Tv("method", 5);
        var call = new JsonObject { ["k"] = "clrInstance", ["method"] = "Exchange",
            ["argTypes"] = new JsonArray(TypeJson.Write(ordinary)),
            ["args"] = new JsonArray(new JsonObject { ["k"] = "callStatic", ["method"] = "EvaluateOnce" }),
            ["ret"] = TypeJson.Write(ordinary) };
        var adapted = Adapt(call, new TypeNode[] { storage }, storage);
        if (TypeJson.Read(adapted["type"]) != ordinary || !ReferenceEquals(adapted["e"], call)
            || TypeJson.Read(call["args"][0]["type"]) != storage
            || TypeJson.Read(call["argTypes"][0]) != storage || TypeJson.Read(call["ret"]) != storage
            || call["args"][0]["e"]["method"].GetValue<string>() != "EvaluateOnce")
            throw new InvalidOperationException("Intrinsic extension lost its ordinary/storage signature boundary");
        var unchanged = new JsonObject { ["args"] = new JsonArray(new JsonObject { ["k"] = "local", ["name"] = "x" }),
            ["argTypes"] = new JsonArray(TypeJson.Write(ordinary)), ["ret"] = TypeJson.Fqn("kotlin.Unit") };
        if (!ReferenceEquals(Adapt(unchanged, new TypeNode[] { ordinary }, new TypeNode.Fqn("System.Void")), unchanged)
            || unchanged["args"][0]["k"].GetValue<string>() != "local")
            throw new InvalidOperationException("Intrinsic extension converted an unchanged slot or a void result");
        Console.WriteLine("[intrinsic extension representation] self-test OK (storage arguments, ordinary result, single evaluation, unchanged slots)");
    }
}
