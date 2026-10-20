using System;
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
    internal static JsonObject Literal(JsonObject literal)
    {
        var map = literal["k"]?.GetValue<string>() == "newMap";
        var key = TypeJson.Read(literal[map ? "keyType" : "elem"]);
        var types = map ? new[] { key, TypeJson.Read(literal["valType"]) } : new[] { key };
        var storage = new TypeNode.Fqn(map ? "System.Collections.Generic.Dictionary" : "System.Collections.Generic.HashSet", types);
        var name = CallEvalLowering.FreshBindingId();
        JsonObject Local() => new() { ["k"] = "local", ["name"] = name };
        var statements = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(storage),
            ["init"] = Allocate(storage, key),
        });
        void Accumulate(params JsonNode[] values) => statements.Add(new JsonObject {
            ["k"] = "exprStmt",
            ["expr"] = new JsonObject {
                ["k"] = "clrInstance", ["type"] = TypeJson.Write(storage),
                ["method"] = map ? "set_Item" : "Add", ["recv"] = Local(),
                ["argTypes"] = new JsonArray(types.Select(TypeJson.Write).ToArray()),
                ["ret"] = TypeJson.Fqn(map ? "void" : "System.Boolean"),
                ["args"] = new JsonArray(values.Select(value => value.DeepClone()).ToArray()),
            },
        });
        if (map)
            foreach (var entry in ((JsonArray)literal["entries"]).OfType<JsonObject>())
                Accumulate(entry["key"], entry["value"]);
        else
            foreach (var element in (JsonArray)literal["elems"]) Accumulate(element);
        return new JsonObject {
            ["k"] = "valueBlock", ["type"] = TypeJson.Write(storage),
            ["stmts"] = statements, ["result"] = Local(),
        };
    }
}
