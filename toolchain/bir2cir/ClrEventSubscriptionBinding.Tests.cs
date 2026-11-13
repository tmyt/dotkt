using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ClrEventSubscriptionBinding
{
    internal static void SelfTest()
    {
        var input = JsonNode.Parse("""
        {"fileClass":"EventFrames","types":[{"kind":"class","name":"Owner",
          "typeParams":[{"name":"O","constraints":[{"t":"fqn","name":"System.IDisposable"}]}],
          "methods":[{"name":"create","typeParams":[{"name":"M","constraints":[{"t":"fqn","name":"System.ICloneable"}]}],
          "body":[{"k":"newSuspendLambda","typeFrame":"dense","arity":0,
            "typeParams":[{"name":"CapturedM","constraints":[{"t":"fqn","name":"System.ICloneable"}]},
                          {"name":"CapturedO","constraints":[{"t":"fqn","name":"System.IDisposable"}]}],
            "typeArgs":[{"t":"tv","scope":"method","i":0},{"t":"tv","scope":"type","i":0}],
            "params":[],"captures":[],"suspendRet":{"t":"fqn","name":"kotlin.Unit"},
            "funcType":{"t":"fn","suspend":true,"ret":{"t":"fqn","name":"kotlin.Unit"},"params":[]},
            "body":[{"k":"exprStmt","expr":{"k":"clrEventGet","name":"Changed","static":false,
              "type":{"t":"fqn","name":"Native.Source","args":[{"t":"tv","scope":"method","i":0},{"t":"tv","scope":"type","i":1}]},
              "recv":{"k":"local","name":"source"},
              "sty":{"t":"fqn","name":"kotlin.clr.ClrEvent","args":[{"t":"fqn","name":"System.Action`1","args":[{"t":"tv","scope":"method","i":0}]}]}}}]}]}]}]}
        """);
        var lambda = input["types"][0]["methods"][0]["body"][0];
        var owner = input["types"][0]["methods"][0];
        var eventRead = lambda["body"][0]["expr"];
        var outerRead = eventRead.DeepClone();
        outerRead["type"]["args"][1]["i"] = 0;
        // A captured handle is evaluated in the constructing caller, not in the carrier's dense frame.
        lambda["captures"] = new JsonArray(
            new JsonObject { ["name"] = "source", ["type"] = eventRead["type"].DeepClone() },
            new JsonObject { ["name"] = "handle", ["type"] = outerRead["sty"].DeepClone() });
        lambda["capValues"] = new JsonArray(
            new JsonObject { ["k"] = "local", ["name"] = "source", ["sty"] = outerRead["type"].DeepClone() }, outerRead);
        owner["params"] = new JsonArray(new JsonObject { ["name"] = "source", ["type"] = outerRead["type"].DeepClone() });
        var references = ReferenceMetadataIndex.Build(Array.Empty<string>());
        void Check(JsonNode source)
        {
            Apply(source, references, new Dictionary<(string Owner, string Event), TypeNode>(), out var handles);
            var callbacks = handles.SelectMany(handle => handle["result"]["args"].AsArray()).ToArray();
            if (callbacks.Length != 4) throw new InvalidOperationException("Event frame fixture did not exercise both source locations");
            foreach (var callback in callbacks)
            {
                var parameters = callback["synthClass"]["typeParams"].AsArray()
                    .Select(TypeParameterDeclaration.Read).ToArray();
                if (parameters.Length != 2 || parameters[0].Constraints?.Single() != new TypeNode.Fqn("System.ICloneable")
                    || parameters[1].Constraints?.Single() != new TypeNode.Fqn("System.IDisposable"))
                    throw new InvalidOperationException("Event callbacks lost their exact generic declaration frame");
            }
        }
        Check(input);
        var classInput = input.DeepClone();
        var classOwner = classInput["types"][0]["methods"][0];
        var sourceCarrier = classOwner["body"][0];
        var classBody = sourceCarrier["body"].DeepClone();
        classBody[0]["expr"]["type"]["args"][0]["scope"] = "type";
        classBody[0]["expr"]["sty"]["args"][0]["args"][0]["scope"] = "type";
        var fields = sourceCarrier["captures"].DeepClone();
        fields[0]["type"]["args"][0]["scope"] = "type";
        fields[1]["type"]["args"][0]["args"][0]["scope"] = "type";
        var payload = new JsonObject {
            ["name"] = "Payload", ["typeParams"] = sourceCarrier["typeParams"].DeepClone(),
            ["fields"] = fields, ["params"] = new JsonArray(),
            ["ret"] = sourceCarrier["suspendRet"].DeepClone(), ["body"] = classBody,
        };
        ClosureSynthesis.MarkPreboundFrame(payload);
        classOwner["body"][0] = new JsonObject {
            ["k"] = "newClosure", ["synthClass"] = payload,
            ["captures"] = sourceCarrier["capValues"].DeepClone(),
            ["typeArgs"] = sourceCarrier["typeArgs"].DeepClone(),
        };
        Check(classInput);
        // A source carrier's body still uses its original lexical TV positions, not the dense capture indices.
        lambda.AsObject().Remove("typeFrame");
        eventRead["type"]["args"][1]["i"] = 0;
        lambda["captures"][0]["type"]["args"][1]["i"] = 0;
        Check(input);
        ForwardedOwnersSelfTest();
        references.DisposeNet();
        Console.WriteLine("[event callback frames] self-test OK (dense, lexical and prebound class frames; caller construction remains separate)");
    }
}
