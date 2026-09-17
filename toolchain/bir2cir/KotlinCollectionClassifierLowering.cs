using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A compiler-provided CLR storage face is not evidence of Kotlin MutableCollection membership.
// Apply the declaration-owned nominal guard while the tested Kotlin classifier is still explicit.
static class KotlinCollectionClassifierLowering
{
    const string RuntimeOwner = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    const string Matches = "kotlinCollectionMatches";
    const string CastCandidate = "kotlinCollectionCastCandidate";

    public static void Apply(JsonNode node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(node, names);
        var next = 0;
        Rewrite(node, names, ref next);
    }

    static void Rewrite(JsonNode node, HashSet<string> names, ref int next)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Rewrite(child, names, ref next);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) Rewrite(child, names, ref next);
        if (Text(obj["k"]) is not ("isInst" or "isInstRef" or "cast") || obj["e"] is not JsonObject operand) return;
        obj.Remove("reifiedTypeOperand");
        var witness = obj[KotlinTypeWitness.OperandKey]?.DeepClone();
        obj.Remove(KotlinTypeWitness.OperandKey);
        if (witness == null)
        {
            var flags = KotlinTypeWitness.Flags(TypeJson.Read(obj["type"]));
            if ((flags & ~1) == 0) return;
            witness = KotlinTypeWitness.Constant(flags);
        }
        var nullableObject = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        JsonObject ClassRef(string name) => new() { ["k"] = "classRef", ["type"] = TypeJson.Fqn(name) };
        JsonObject Call(string helper, TypeNode result, JsonNode value) => new() {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(RuntimeOwner), ["method"] = helper,
            ["sig"] = new JsonArray(TypeJson.Write(nullableObject), TypeJson.Fqn("kotlin.Int"),
                TypeJson.Write(runtimeType), TypeJson.Write(runtimeType), TypeJson.Write(runtimeType),
                TypeJson.Write(runtimeType), TypeJson.Write(runtimeType)),
            ["ret"] = TypeJson.Write(result),
            ["args"] = new JsonArray(value.DeepClone(), witness.DeepClone(),
                ClassRef("System.Collections.Generic.IDictionary`2"), ClassRef("System.Collections.Generic.IReadOnlyDictionary`2"),
                ClassRef("System.Collections.Generic.ISet`1"), ClassRef("System.Collections.Generic.IReadOnlySet`1"),
                ClassRef("System.Collections.IList")),
        };
        if (Text(obj["k"]) == "cast")
        {
            if (Text(operand["k"]) == "callStatic" && Text(operand["method"]) == CastCandidate
                && TypeJson.OwnerName(operand["owner"]) == RuntimeOwner) return;
            obj["e"] = Call(CastCandidate, nullableObject, operand);
            return;
        }

        // A sentinel object would still satisfy an erased reified T=object. Membership must guard the
        // physical test independently, including nullable tests. Evaluate arbitrary operands exactly once.
        string temp;
        do temp = "dotkt$collectionClassifier$value$" + next++; while (!names.Add(temp));
        var local = new JsonObject { ["k"] = "local", ["name"] = temp };
        var physicalTest = (JsonObject)obj.DeepClone();
        physicalTest["e"] = local.DeepClone();
        var matches = Call(Matches, new TypeNode.Fqn("kotlin.Boolean"), local);
        var target = TypeJson.Read(physicalTest["type"]);
        var safeCastResult = target is TypeNode.Nullable ? target : new TypeNode.Nullable(target);
        JsonObject result = Text(obj["k"]) == "isInst"
            ? new JsonObject {
                ["k"] = "cond", ["cond"] = matches, ["then"] = physicalTest,
                ["else"] = new JsonObject {
                    ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Boolean"), ["value"] = false,
                },
            }
            : new JsonObject {
                ["k"] = "cond", ["type"] = TypeJson.Write(safeCastResult),
                ["cond"] = matches, ["then"] = physicalTest,
                ["else"] = new JsonObject {
                    ["k"] = "const", ["type"] = TypeJson.Write(nullableObject), ["value"] = null,
                },
            };
        var statements = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = temp, ["type"] = TypeJson.Write(nullableObject),
            ["init"] = operand.DeepClone(),
        });
        obj.Clear();
        obj["k"] = "valueBlock";
        obj["stmts"] = statements;
        obj["result"] = result;
    }

    static void CollectNames(JsonNode node, HashSet<string> names)
    {
        if (node is JsonObject obj)
        {
            if (Text(obj["name"]) is string name) names.Add(name);
            foreach (var child in obj.Select(pair => pair.Value)) CollectNames(child, names);
        }
        else if (node is JsonArray array)
            foreach (var child in array) CollectNames(child, names);
    }

    static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
