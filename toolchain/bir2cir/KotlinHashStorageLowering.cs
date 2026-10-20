using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin-owned hash storage uses the same equality/hash contract as Kotlin calls.
// Explicit projected CLR constructors retain their selected native comparer.
static class KotlinHashStorageLowering
{
    const string StorageSignature = "kotlinHashStorageSignature";

    internal static void RequestComparer(JsonObject construction) => construction[StorageSignature] = true;

    // These generated native signatures are applications of the storage owner's frame, not independent source
    // declaration types. Close them only after Kotlin value representation (including existential carriers) settles.
    internal static void Complete(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Complete(child);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToList()) if (child != null) Complete(child);
        if (!obj.Remove(StorageSignature)) return;
        var storage = (TypeNode.Fqn)TypeJson.Read(obj["type"]);
        if (obj["k"]!.GetValue<string>() == "newClr")
            AddComparer((JsonArray)obj["args"], (JsonArray)obj["argTypes"], storage.Args[0]);
        else
            obj["argTypes"] = new JsonArray(storage.Args.Select(TypeJson.Write).ToArray());
    }

    static void AddComparer(JsonArray args, JsonArray signature, TypeNode key)
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

    internal static JsonObject Allocate(TypeNode type)
    {
        return new JsonObject {
            ["k"] = "newClr", ["type"] = TypeJson.Write(type),
            ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
            [StorageSignature] = true,
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
            ["init"] = Allocate(storage),
        });
        void Accumulate(params JsonNode[] values) => statements.Add(new JsonObject {
            ["k"] = "exprStmt",
            ["expr"] = new JsonObject {
                ["k"] = "clrInstance", ["type"] = TypeJson.Write(storage),
                ["method"] = accumulator, ["recv"] = Local(),
                ["argTypes"] = new JsonArray(types.Select(TypeJson.Write).ToArray()),
                [StorageSignature] = true,
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
