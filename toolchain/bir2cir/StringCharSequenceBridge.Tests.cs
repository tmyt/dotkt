using System;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

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
              {"name":"nullable", "type":{"t":"nullable","of":{"t":"fqn","name":"kotlin.String"}}},
              {"name":"boxed", "type":{"t":"fqn","name":"object"}}
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
               "else":{"k":"const","type":{"t":"nullable","of":{"t":"fqn","name":"dotkt$CharSequence"}},"value":null}}},
              {"k":"return", "value":{"k":"cast","type":{"t":"fqn","name":"dotkt$CharSequence"},
               "e":{"k":"local","name":"boxed"}}},
              {"k":"return", "value":{"k":"cast","type":{"t":"array","elem":{"t":"fqn","name":"dotkt$CharSequence"}},
               "e":{"k":"local","name":"boxed"}}}
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
        var restored = body[4]["value"];
        Require(Str(restored["k"]) == "valueBlock" && restored["stmts"].AsArray().Count == 1
            && Str(restored["stmts"][0]["init"]["name"]) == "boxed"
            && AdapterNode(restored["result"]["then"])
            && Str(restored["result"]["else"]["else"]["k"]) == "cast",
            "opaque consumption must evaluate once, adapt strings, and preserve the existing/null view");
        Require(Str(body[5]["value"]["k"]) == "cast",
            "a CharSequence array must retain its array cast rather than restore one scalar view");
        var member = new BirCallInstance { OwnerType = new TypeNode.Fqn("dotkt$CharSequence"),
            Method = "length", Prop = "get", Args = Array.Empty<IBirExpression>(),
            Recv = new BirCallInstance { OwnerType = new TypeNode.Fqn("ImportedSlot"), Method = "read",
                Args = Array.Empty<IBirExpression>(), Ret = new TypeNode.Fqn("dotkt$CharSequence") } };
        var stringMember = CharSeqStringLowering.Apply(member, null);
        Require(stringMember is BirPropertyGet { Recv.Value: BirCallStatic {
                Method.Value: "toString", Args.Value: [BirCallInstance { Method.Value: "read" }] } },
            "a String member must normalize an imported generic result before native consumption");
        member.Recv = new BirConstant { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral("ok") };
        Require(CharSeqStringLowering.Apply(member, null) is BirPropertyGet,
            "a proven String receiver must retain its direct native member lowering");
        Console.WriteLine("[selftest] String/CharSequence receiver and conditional boundaries: OK");
    }
}
