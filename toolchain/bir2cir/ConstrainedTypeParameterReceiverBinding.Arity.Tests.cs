using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class ConstrainedTypeParameterReceiverBinding
{
    internal static void AritySnapshotSelfTest()
    {
        var root = JsonNode.Parse("""
          {"types":[{"name":"Plain","types":[{"name":"Nested","typeParams":["T"]}]},
                    {"name":"Duplicate","typeParams":["T"]},
                    {"name":"Duplicate","typeParams":[]},
                    {"name":"Receiver","typeParams":[{"name":"T","constraints":[
                      {"t":"fqn","name":"Nested","args":[{"t":"fqn","name":"Plain"}]}]}],
                     "methods":[{"name":"use","params":[],"body":[
                       {"k":"callInstance","ownerType":{"t":"fqn","name":"Nested"},"name":"run",
                        "recv":{"k":"local","name":"x","sty":{"t":"tv","scope":"type","i":0}}}]}]}]}
          """);
        var roots = new[] { root };
        var snapshot = CollectTypeArity(roots);
        if (snapshot.Count != 4 || snapshot["Plain"] != 0 || snapshot["Nested"] != 1
            || snapshot["Duplicate"] != 0 || snapshot["Receiver"] != 1)
            throw new InvalidOperationException("Declaration arities lost nested types or traversal-order overwrite");

        // Multiple body/constraint rewrites can share the snapshot without changing their result.
        var receiver = root["types"][3].AsObject();
        var expected = receiver.DeepClone().AsObject();
        CloseTypeOwners(expected, CollectTypeArity(roots));
        CloseTypeOwners(receiver, snapshot);
        CloseTypeOwners(receiver, snapshot);
        var method = receiver["methods"][0].AsObject();
        CloseMethodOwners(method, receiver, snapshot, new JsonArray(), refs: null);
        if (!JsonNode.DeepEquals(receiver, expected)
            || TypeJson.Read(method["body"][0]["ownerType"]) != new TypeNode.Fqn("Nested", new TypeNode[] { new TypeNode.Fqn("Plain") }))
            throw new InvalidOperationException("Shared arities changed owner closing or its ordering");

        // Structural mutations end the lifetime. A new snapshot observes added/removed/renamed types and changed
        // parameter counts, while the old snapshot remains a value (no retained root or hidden cache).
        var declarations = root["types"].AsArray();
        var nested = declarations[0]["types"][0].AsObject();
        nested["name"] = "Renamed";
        nested["typeParams"].AsArray().Add("U");
        declarations.RemoveAt(2);
        declarations.Add(new JsonObject { ["name"] = "Added", ["typeParams"] = new JsonArray("A") });
        var rebuilt = CollectTypeArity(roots);
        if (snapshot.ContainsKey("Renamed") || snapshot.ContainsKey("Added") || snapshot["Nested"] != 1
            || rebuilt.ContainsKey("Nested") || rebuilt["Renamed"] != 2 || rebuilt["Added"] != 1
            || rebuilt["Duplicate"] != 1
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), rebuilt) != null
            || ClosedOwner(new TypeNode.Fqn("Duplicate"), snapshot) == null)
            throw new InvalidOperationException("Declaration arity snapshot was not rebuilt across structural mutation");
        if (CollectTypeArity(Array.Empty<JsonNode>()).Any())
            throw new InvalidOperationException("Declaration arities leaked into a later compilation");
        Console.WriteLine("[declaration arities] self-test OK (shared closing, nested/order, mutation/rebuild, isolation)");
    }
}
