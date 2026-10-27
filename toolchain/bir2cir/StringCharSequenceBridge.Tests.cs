using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class StringCharSequenceBridge
{
    internal static void SelfTest()
    {
        var source = JsonNode.Parse("""
        {
          "methods": [{
            "name": "boundaries", "params": [
              {"name":"text", "type":{"t":"fqn","name":"kotlin.String"}},
              {"name":"custom", "type":{"t":"fqn","name":"dotkt$CharSequence"}},
              {"name":"nullable", "type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.String"}}}
            ],
            "ret":{"t":"fqn","name":"object"},
            "body":[
              {"k":"callInstance", "ownerType":{"t":"fqn","name":"dotkt$CharSequence"},
               "method":"get", "recv":{"k":"local","name":"text"}, "sig":[], "args":[]},
              {"k":"callInstance", "ownerType":{"t":"fqn","name":"dotkt$CharSequence"},
               "method":"get", "recv":{"k":"local","name":"custom"}, "sig":[], "args":[]},
              {"k":"return", "value":{"k":"cond", "type":{"t":"fqn","name":"dotkt$CharSequence"},
               "cond":{"k":"const","type":{"t":"fqn","name":"kotlin.Boolean"},"value":true},
               "then":{"k":"local","name":"text"}, "else":{"k":"local","name":"custom"}}},
              {"k":"return", "value":{"k":"cond", "type":{"t":"nullable","of":{"t":"fqn","name":"dotkt$CharSequence"}},
               "cond":{"k":"const","type":{"t":"fqn","name":"kotlin.Boolean"},"value":true},
               "then":{"k":"local","name":"nullable"},
               "else":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"dotkt$CharSequence"}},"value":null}}}
            ]
          }], "types":[]
        }
        """);
        var lowered = Apply(source, null, null, out _);
        var body = lowered["methods"][0]["body"].AsArray();
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("CharSequence boundary selftest: " + message);
        }
        bool AdapterNode(JsonNode value) => Str(value?["k"]) == "new"
            && TypeJson.OwnerName(value?["type"]) == Adapter;
        Require(AdapterNode(body[0]["recv"]), "resolved interface receiver must adapt String");
        Require(Str(body[1]["recv"]["k"]) == "local", "custom receiver identity must remain unchanged");
        var join = body[2]["value"];
        Require(AdapterNode(join["then"]) && Str(join["else"]["k"]) == "local",
            "object consumer must not bypass the declared conditional interface slot");
        var nullable = body[3]["value"];
        var nullableBranch = nullable["then"];
        var nullableCore = Str(nullableBranch["k"]) == "valueBlock" ? nullableBranch["result"] : nullableBranch;
        Require(Str(nullableCore["k"]) == "cond" && nullableCore["then"]["value"] == null
            && AdapterNode(nullableCore["else"]), "nullable branch must preserve null and adapt non-null String");
        Require(nullable["else"]["value"] == null, "null branch must remain null");
        Console.WriteLine("[selftest] String/CharSequence receiver and conditional boundaries: OK");
    }
}
