using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class GenericArrayStorageLowering
{
    static void FreshNamesSelfTest()
    {
        void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Array fresh names: " + message);
        }
        var arrayType = new TypeNode.Fqn("System.Array");
        var objectType = new TypeNode.Fqn("System.Object");
        JsonObject LoopNode(string variable) => new() {
            ["k"] = "forArray", ["array"] = Local("array", arrayType),
            ["var"] = variable, ["elem"] = TypeJson.Write(objectType), ["body"] = new JsonArray(),
        };
        var inner = LoopNode("inner");
        var outer = LoopNode("outer");
        outer["body"].AsArray().Add(inner);
        var store = new JsonObject {
            ["k"] = "arraySet", ["array"] = Local("receiverEffect", arrayType),
            ["index"] = Local("indexEffect", new TypeNode.Fqn("System.Int32")),
            ["value"] = Local("valueEffect", objectType),
        };
        var cast = new JsonObject { ["k"] = "cast", ["e"] = Local("value", objectType) };
        var file = new JsonObject {
            ["methods"] = new JsonArray(new JsonObject {
                ["body"] = new JsonArray(outer, store, cast),
            }, new JsonObject {
                // Both declarations and loop variables in a later method must be reserved up front.
                ["params"] = new JsonArray(new JsonObject { ["name"] = "$arrayStorage$0" }),
                ["body"] = new JsonArray(new JsonObject { ["name"] = "$arrayIndex$0" },
                    new JsonObject { ["var"] = "$arrayLength$0" },
                    new JsonObject { ["name"] = "$arrayValue$0" },
                    new JsonObject { ["name"] = "$arrayComponent$0" },
                    new JsonObject { ["name"] = "$arrayCast$0" },
                    new JsonObject { ["name"] = "$arrayCastType$0" }),
            }),
        };
        var names = new FreshNames(file);
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        Expect(LowerOperation(inner, refs, names) && LowerOperation(outer, refs, names), "nested loops not lowered");
        Expect(Text(outer["body"]?[0]?["name"]) == "$arrayStorage$2"
            && Text(outer["body"]?[2]?["body"]?[1]?["body"]?[0]?["name"]) == "$arrayStorage$1",
            "post-order loop allocations collided or changed order");
        Expect(LowerOperation(store, refs, names), "store not lowered");
        var statements = store["stmts"].AsArray();
        Expect(Text(statements[0]?["name"]) == "$arrayStorage$3"
            && Text(statements[1]?["name"]) == "$arrayIndex$3"
            && Text(statements[2]?["name"]) == "$arrayValue$1"
            && Text(statements[3]?["name"]) == "$arrayComponent$1", "store names collided");
        Expect(statements.Take(3).Select(node => Text(node?["init"]?["name"]))
            .SequenceEqual(new[] { "receiverEffect", "indexEffect", "valueEffect" }),
            "store receiver/index/value evaluation order changed");
        LowerTypeOperation(cast, new TypeNode.Array(new TypeNode.Star()), null, null, refs,
            type => type, _ => false, names);
        Expect(Text(cast["stmts"]?[0]?["name"]) == "$arrayCast$1"
            && Text(cast["stmts"]?[2]?["branches"]?[0]?["body"]?[0]?["name"]) == "$arrayCastType$1",
            "cast names collided with later declarations");

        // A newly synthesized subtree must be reserved before attachment/rewrite. The allocator does not
        // discover arbitrary mutations, nor depend on Parent links while operating on detached nodes.
        var detached = LoopNode("$arrayStorage$4");
        names.Reserve(detached);
        Expect(LowerOperation(detached, refs, names)
            && Text(detached["body"]?[0]?["name"]) == "$arrayStorage$5", "detached rewrite lost destination names");
        outer.Clear();
        Expect(names.Next("$arrayStorage$") == "$arrayStorage$6", "removed/generated reservations were reused");
        var secondFile = new FreshNames(new JsonObject());
        Expect(secondFile.Next("$arrayStorage$") == "$arrayStorage$0", "allocator leaked across files");
        var lazyFile = new JsonObject();
        var lazyNames = new FreshNames(lazyFile);
        lazyFile["name"] = "$arrayStorage$0";
        Expect(lazyNames.Next("$arrayStorage$") == "$arrayStorage$1", "file was not seeded at first allocation");
        var nextPhase = new FreshNames(file);
        Expect(nextPhase.Next("$arrayStorage$") == "$arrayStorage$1", "new phase retained removed names");
        EntryPointFreshNamesSelfTest(refs);
        TypedGenericArrayStorageLowering.SelfTest(refs);
        Console.WriteLine("[array fresh names] self-test OK (file lifetime, later declarations, detached rewrites, reservations, evaluation order)");
    }

    static void EntryPointFreshNamesSelfTest(ReferenceMetadataIndex refs)
    {
        JsonObject File(string fileClass, string lateName) => new() {
            ["fileClass"] = fileClass, ["types"] = new JsonArray(),
            ["methods"] = new JsonArray(new JsonObject {
                ["name"] = "probe", ["ret"] = Type("void"),
                ["params"] = new JsonArray(new JsonObject { ["name"] = "array", ["type"] = Type("System.Array") }),
                ["body"] = new JsonArray(new JsonObject {
                    ["k"] = "forArray", ["array"] = Local("array", new TypeNode.Fqn("System.Array")),
                    ["var"] = "element", ["elem"] = Type("System.Object"), ["body"] = new JsonArray(),
                }, Var(lateName, new TypeNode.Fqn("System.Int32"), Int(0))),
            }),
        };
        foreach (var physical in new[] { false, true })
        {
            var first = File("FirstArrayFile", "$arrayIndex$0");
            var second = File("SecondArrayFile", "$arrayStorage$1");
            var roots = new JsonNode[] { first, second };
            if (physical)
                PhysicalValueCoercion.ApplyAll(roots,
                    () => throw new InvalidOperationException("Unexpected array Unit conversion"),
                    type => type.Name == "System.Int32", references: refs);
            else
                FBoundStarProjectionErasure.ApplyAll(roots, refs);
            var firstBody = first["methods"]?[0]?["body"]?[0]?["body"];
            var secondBody = second["methods"]?[0]?["body"]?[0]?["body"];
            if (Text(firstBody?[0]?["name"]) != "$arrayStorage$0"
                || Text(firstBody?[2]?["var"]) != "$arrayIndex$1"
                || Text(secondBody?[0]?["name"]) != "$arrayStorage$0")
                throw new InvalidOperationException("Array allocator entrypoint lost file lifetime or later declarations: "
                    + (physical ? "physical coercion" : "FBound"));
        }
    }
}
