using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin-owned hash storage uses the same equality/hash contract as Kotlin calls.
// Explicit projected CLR constructors retain their selected native comparer.
static class KotlinHashStorageLowering
{
    internal static void AddComparer(JsonArray args, JsonArray signature, TypeNode key)
    {
        var comparer = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.NativeHashComparer", new[] { key });
        signature.Add(TypeJson.Write(comparer));
        args.Add(new JsonObject {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn("DotKt.Runtime.CompilerServices.KotlinHashComparerKt"),
            ["method"] = "kotlinHashComparer",
            ["typeArgs"] = new JsonArray(TypeJson.Write(key)),
            ["sig"] = new JsonArray(),
            ["ret"] = TypeJson.Write(comparer),
            ["args"] = new JsonArray(),
        });
    }

    internal static JsonObject Allocate(TypeNode type, TypeNode key)
    {
        var args = new JsonArray();
        var signature = new JsonArray();
        AddComparer(args, signature, key);
        return new JsonObject {
            ["k"] = "newClr", ["type"] = TypeJson.Write(type),
            ["argTypes"] = signature, ["args"] = args,
        };
    }

    // Express literals using existing allocation/call nodes, with every constructor argument explicit.
    // No emitter-side knowledge of Kotlin hashing or a hidden constructor operand is necessary.
    internal static JsonObject MapLiteral(TypeNode key, TypeNode value,
        IEnumerable<(JsonNode Key, JsonNode Value)> entries) => Literal(
            new TypeNode.Fqn("System.Collections.Generic.Dictionary", new[] { key, value }),
            "set_Item", "void", entries.Select(entry => new[] { entry.Key, entry.Value }));

    internal static JsonObject SetLiteral(TypeNode element, IEnumerable<JsonNode> elements) => Literal(
        new TypeNode.Fqn("System.Collections.Generic.HashSet", new[] { element }),
        "Add", "System.Boolean", elements.Select(value => new[] { value }));

    static JsonObject Literal(TypeNode.Fqn storage, string accumulator, string resultType,
        IEnumerable<JsonNode[]> sourceOperands)
    {
        var types = storage.Args;
        var key = types[0];
        var name = CallEvalLowering.FreshBindingId();
        JsonObject Local() => new() { ["k"] = "local", ["name"] = name };
        var statements = new JsonArray();
        JsonObject Capture(JsonNode value, TypeNode slot)
        {
            var captured = CallEvalLowering.FreshBindingId();
            // Snapshot every argument before storage can invoke user equality/hash callbacks.
            // Keep the value erased here: the native storage check belongs to insertion, not argument evaluation.
            statements.Add(new JsonObject {
                ["k"] = "var", ["name"] = captured,
                ["type"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))),
                ["init"] = value.DeepClone(),
            });
            return new JsonObject {
                ["k"] = "cast", ["type"] = TypeJson.Write(slot),
                ["e"] = new JsonObject { ["k"] = "local", ["name"] = captured },
            };
        }
        var operands = new List<JsonNode[]>();
        foreach (var arguments in sourceOperands)
            operands.Add(arguments.Select((value, index) => (JsonNode)Capture(value, types[index])).ToArray());
        statements.Add(new JsonObject {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(storage),
            ["init"] = Allocate(storage, key),
        });
        void Accumulate(params JsonNode[] values) => statements.Add(new JsonObject {
            ["k"] = "exprStmt",
            ["expr"] = new JsonObject {
                ["k"] = "clrInstance", ["type"] = TypeJson.Write(storage),
                ["method"] = accumulator, ["recv"] = Local(),
                ["argTypes"] = new JsonArray(types.Select(TypeJson.Write).ToArray()),
                ["ret"] = TypeJson.Fqn(resultType),
                ["args"] = new JsonArray(values.Select(value => value.DeepClone()).ToArray()),
            },
        });
        foreach (var values in operands) Accumulate(values);
        return new JsonObject {
            ["k"] = "valueBlock", ["type"] = TypeJson.Write(storage),
            ["stmts"] = statements, ["result"] = Local(),
        };
    }
}
