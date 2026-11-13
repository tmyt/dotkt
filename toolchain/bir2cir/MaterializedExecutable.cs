using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Admission contract for executable Kotlin-vocabulary graphs synthesized after the source-file normalization point.
// A producer publishes the graph only after this ordered normalization: constructed receiver-relative result types
// are closed first, then a resulting `Nothing` value is terminated before CLR erasure can turn it into `object`.
// Keeping the order and entry point here prevents each bridge producer from recreating a partial late-pass schedule.
static class MaterializedExecutable
{
    public static T Normalize<T>(T root) where T : JsonNode
    {
        ConstructedMemberReturnSubstitution.ApplyMaterialized(root);
        return NothingValueTermination.ApplyMaterialized(root);
    }

    // A compiler-authored forwarder has exactly one selected call with this/local
    // operands. Normalize that returned value at construction, without admitting
    // an arbitrary body through a second graph or replaying a late pass schedule.
    internal static IBirExpression ReturnedCall(BirCallInstance call)
    {
        ConstructedMemberReturnSubstitution.ApplyCall(call);
        return NothingValueTermination.Consume(call);
    }

    public static void SelfTest()
    {
        var bridge = new JsonObject
        {
            ["body"] = new JsonArray(new JsonObject
            {
                ["k"] = "return",
                ["value"] = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = new JsonObject
                    {
                        ["t"] = "fqn", ["name"] = "Example.Box",
                        ["args"] = new JsonArray(new JsonObject { ["t"] = "fqn", ["name"] = "kotlin.Nothing" }),
                    },
                    ["ret"] = new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = 0 },
                },
            }),
        };

        Normalize(bridge);
        var terminated = bridge["body"]?[0]?["value"] as JsonObject;
        var call = terminated?["value"] as JsonObject;
        if (terminated?["k"]?.GetValue<string>() != "throwExpr"
            || TypeJson.Read(call?["ret"]) is not TypeNode.Fqn { Name: "kotlin.Nothing" })
            throw new InvalidOperationException("materialized executable normalization self-test failed");
        var typed = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("Example.Box", new TypeNode[] { new TypeNode.Fqn("kotlin.Nothing") }),
            Ret = new TypeNode.Tv("type", 0),
        };
        if (ReturnedCall(typed) is not BirThrowExpression { Value.HasValue: true } typedTermination
            || !ReferenceEquals(typedTermination.Value.Value, typed)
            || typed.Ret.Value is not TypeNode.Fqn { Name: "kotlin.Nothing" })
            throw new InvalidOperationException("typed returned-call admission changed result closure or Nothing termination");
    }
}
