using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class UnsafeAccessorLowering
{
    internal static void FamilyAccessSelfTest()
    {
        var root = JsonNode.Parse("""
          {"fileClass":"FamilyProbe","methods":[],"types":[
            {"name":"Container","kind":"class","vis":"public","methods":[]},
            {"name":"Base","nestedIn":"Container","kind":"class","vis":"private","typeParams":["T"],
             "fields":[{"name":"slot","vis":"protected","type":{"t":"tv","scope":"type","i":0}}],
             "methods":[{"name":"read","vis":"protected","params":[],"ret":{"t":"tv","scope":"type","i":0}},
                        {"name":"hidden","vis":"private","params":[],"ret":{"t":"tv","scope":"type","i":0}}]},
            {"name":"Derived","nestedIn":"Container","kind":"class","vis":"private","typeParams":["T"],
             "base":{"t":"fqn","name":"Base","args":[{"t":"tv","scope":"type","i":0}]},
             "methods":[{"name":"use","typeParams":["U"],"params":[
                {"name":"derived","type":{"t":"fqn","name":"Derived","args":[{"t":"tv","scope":"method","i":0}]}},
                {"name":"base","type":{"t":"fqn","name":"Base","args":[{"t":"tv","scope":"method","i":0}]}}],"body":[]}]},
            {"name":"Capture","nestedIn":"Derived","kind":"class","capturedTypeParams":["T"],
             "fields":[{"name":"outer","type":{"t":"fqn","name":"Derived","args":[{"t":"tv","scope":"type","i":0}]}}],
             "methods":[{"name":"invoke","params":[],"body":[]}]},
            {"name":"Face","kind":"interface","typeParams":["T"],"methods":[]},
            {"name":"Implementation","kind":"class","typeParams":["T"],"interfaces":[
                {"t":"fqn","name":"Face","args":[{"t":"tv","scope":"type","i":0}]}],"methods":[]}]}
          """);
        TypeNode.Fqn Owner(string name, string scope) => new(name, new TypeNode[] { new TypeNode.Tv(scope, 0) });
        JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
        JsonObject Call(JsonNode receiver, string scope, string method = "read") => new()
        {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(Owner("Base", scope)),
            ["recv"] = receiver, ["method"] = method, ["virtual"] = true,
            ["sig"] = new JsonArray(), ["args"] = new JsonArray(),
            ["ret"] = TypeJson.Write(new TypeNode.Tv(scope, 0)),
        };
        var body = root["types"][2]["methods"][0]["body"].AsArray();
        var own = Call(new JsonObject { ["k"] = "this" }, "type");
        var otherConstruction = Call(Local("derived"), "method");
        body.Add(own);
        body.Add(otherConstruction);
        var alias = Call(Local("alias"), "method");
        body.Add(new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray(new JsonObject
        {
            ["k"] = "var", ["name"] = "alias", ["type"] = TypeJson.Write(Owner("Derived", "method")),
            ["init"] = Local("derived"),
        }), ["result"] = alias });
        var field = new JsonObject { ["k"] = "field", ["ownerType"] = TypeJson.Write(Owner("Base", "type")),
            ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = "slot" };
        body.Add(field);
        var bound = new JsonObject { ["k"] = "newBoundDelegate", ["ownerType"] = TypeJson.Write(Owner("Base", "type")),
            ["recv"] = new JsonObject { ["k"] = "this" }, ["method"] = "read", ["sig"] = new JsonArray(),
            ["ret"] = TypeJson.Write(new TypeNode.Tv("type", 0)) };
        body.Add(bound);
        var invalidReceiver = Call(Local("base"), "method");
        var privateCrossing = Call(new JsonObject { ["k"] = "this" }, "type", "hidden");
        body.Add(invalidReceiver);
        body.Add(privateCrossing);
        var captured = Call(new JsonObject { ["k"] = "field", ["ownerType"] = TypeJson.Write(Owner("Capture", "type")),
            ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = "outer" }, "type");
        root["types"][3]["methods"][0]["body"].AsArray().Add(captured);
        var direct = new[] { own, otherConstruction, alias, field, bound, captured };
        var snapshots = direct.Select(node => node.ToJsonString()).ToArray();
        var function = new TypeNode.Fn(false, new TypeNode.Tv("type", 0), new TypeNode[] { new TypeNode.Tv("type", 0) });
        root["types"][1]["methods"].AsArray().Add(new JsonObject { ["name"] = "transform", ["vis"] = "protected",
            ["params"] = new JsonArray(new JsonObject { ["name"] = "block", ["type"] = TypeJson.Write(function) }),
            ["ret"] = TypeJson.Write(function) });
        root["types"].AsArray().Add(new JsonObject { ["name"] = "System.Func", ["kind"] = "class", ["vis"] = "public",
            ["typeParams"] = new JsonArray("T", "R"), ["methods"] = new JsonArray() });
        var functionCall = Call(new JsonObject { ["k"] = "this" }, "type", "transform");
        functionCall["sig"] = new JsonArray(TypeJson.Write(function));
        functionCall["ret"] = TypeJson.Write(function);
        functionCall["args"] = new JsonArray(new JsonObject { ["k"] = "null" });
        body.Add(functionCall);
        var functionSnapshot = functionCall.ToJsonString();
        ApplyAll(new[] { root }, null);
        if (functionCall.ToJsonString() != functionSnapshot)
            throw new InvalidOperationException("A physical function signature lost legal family access");
        for (var index = 0; index < direct.Length; index++)
            if (direct[index].ToJsonString() != snapshots[index])
                throw new InvalidOperationException("Legal family access changed its exact owner/frame/dispatch");
        if (Str(invalidReceiver["k"]) != "callStatic" || Str(privateCrossing["k"]) != "callStatic")
            throw new InvalidOperationException("An arbitrary base receiver or private crossing acquired family access");
        var types = SupertypeGraph.Collect(new[] { root });
        if (!SupertypeGraph.ReachesDeclaration(Owner("Implementation", "type"), Owner("Face", "type"), types, null)
            || SupertypeGraph.ReachesClassDeclaration(Owner("Implementation", "type"), Owner("Face", "type"), types, null))
            throw new InvalidOperationException("Interface implementation granted class-family permission");
        Console.WriteLine("[protected family] self-test OK (lexical context, receiver qualification, exact operands and negative crossings)");
    }
}
