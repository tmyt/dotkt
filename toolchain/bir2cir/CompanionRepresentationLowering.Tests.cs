using System.Text.Json.Nodes;

static class CompanionRepresentationLoweringTests
{
    const string Semantic = "Probe.Owner.<companion:Named>";
    const string Physical = "Probe.Owner.$Named";

    public static void SelfTest()
    {
        var root = JsonNode.Parse("""
        {"types":[
          {"name":"Probe.Owner"},
          {"name":"Probe.Owner.<companion:Named>",
           "kotlinCompanion":{"owner":"Probe.Owner","name":"Named","visibility":"public"}},
          {"name":"Probe.Generic","typeParams":[{"name":"T"}]},
          {"name":"Probe.Generic.<companion:Named>",
           "kotlinCompanion":{"owner":"Probe.Generic","name":"Named","visibility":"internal"}}]}
        """)!;
        var uses = Uses();
        root["body"] = uses;
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        AssertUses(uses);

        // Spliced payloads bind through the same local associations after initial declaration placement.
        var spliced = Uses();
        CompanionRepresentationLowering.BindUses(spliced, local, null, bindExternal: false);
        AssertUses(spliced);
        var stable = spliced.ToJsonString();
        CompanionRepresentationLowering.BindUses(spliced, local, null, bindExternal: false);
        if (spliced.ToJsonString() != stable)
            throw new InvalidOperationException("Companion use binding changed an already-bound payload");

        NestedPlacementOrder();
        Console.WriteLine("[companion representation] self-test OK (uses, selected owners, independent nodes, placement order)");
    }

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    static JsonObject Value(string semantic = Semantic) => new() {
        ["k"] = "companionValue", ["companionType"] = Fqn(semantic),
    };

    static JsonObject Singleton(string physical) => new() {
        ["k"] = "staticField", ["ownerType"] = Fqn(physical),
        ["name"] = "$INSTANCE", ["sty"] = Fqn(physical),
    };

    static JsonObject Member(string kind, string owner = Semantic) => new() {
        ["k"] = kind, ["ownerType"] = Fqn(owner), ["recv"] = Value(),
        ["calleeOwner"] = Fqn("Probe.SelectedOwner"),
        ["declarationId"] = "selected-declaration", ["method"] = "selectedMethod",
        ["name"] = "selectedField", ["sig"] = new JsonArray(Fqn("Probe.Parameter")),
        ["typeArgs"] = new JsonArray(Fqn("Probe.Argument")),
    };

    static JsonArray Uses()
    {
        var uses = new JsonArray(
            new JsonObject { ["t"] = "nullable", ["of"] = Fqn(Semantic) },
            new JsonObject { ["name"] = "Probe.Child", ["semanticOwner"] = Semantic },
            Value(), Value("Probe.Generic.<companion:Named>"));
        // The matching delegate inputs deliberately start with a distinct calleeOwner sentinel to distinguish
        // the receiver-driven rewrite from ordinary child traversal; these are isolated pass-order controls.
        foreach (var kind in new[] { "callInstance", "field", "setField", "setFieldExpr", "lateinitGet", "newBoundDelegate" })
            uses.Add(Member(kind));
        uses.Add(Member("newBoundDelegate", Physical));
        // An inherited member's selected owner must not become the receiver's carrier.
        uses.Add(Member("newBoundDelegate", "Probe.SelectedOwner"));
        var otherReceiver = Member("newBoundDelegate");
        otherReceiver["recv"] = new JsonObject { ["k"] = "local", ["name"] = "ordinaryReceiver" };
        otherReceiver["calleeOwner"] = Fqn(Semantic);
        uses.Add(otherReceiver);
        uses.Add(Fqn("Probe.Unrelated"));
        uses.Add(Value("Probe.Unknown.<companion:Named>"));
        return uses;
    }

    static void AssertUses(JsonArray actual)
    {
        var expected = Uses();
        expected[0]!["of"] = Fqn(Physical);
        expected[1]!["semanticOwner"] = Physical;
        expected[2] = Singleton(Physical);
        expected[3] = Singleton("Probe.Generic$companion$Named");
        for (var i = 4; i <= 10; i++)
        {
            expected[i]!["ownerType"] = Fqn(Physical);
            expected[i]!["recv"] = Singleton(Physical);
            if (expected[i]!["k"]!.GetValue<string>() == "newBoundDelegate")
                expected[i]!["calleeOwner"] = Fqn(Physical);
        }
        expected[11]!["recv"] = Singleton(Physical);
        // An ordinary receiver keeps the real delegate invariant: both selected owner tokens bind together.
        expected[12]!["ownerType"] = Fqn(Physical);
        expected[12]!["calleeOwner"] = Fqn(Physical);
        if (!JsonNode.DeepEquals(actual, expected))
            throw new InvalidOperationException($"Companion binding changed exact use/owner facts: {actual}");

        // JsonNode's single-parent checks already reject direct sharing during insertion. Also pin the
        // resulting attachments and mutation independence explicitly, without relying on serialization alone.
        var owners = new[] { actual[9]!["ownerType"]!, actual[9]!["calleeOwner"]!,
            actual[9]!["recv"]!["ownerType"]!, actual[9]!["recv"]!["sty"]!, actual[10]!["ownerType"]! };
        if (owners.Distinct(ReferenceEqualityComparer.Instance).Count() != owners.Length ||
            owners.Any(node => node.Parent is null))
            throw new InvalidOperationException("Companion physical types must have independent parent-safe nodes");
        owners[0]["name"] = "Probe.Mutated";
        if (owners.Skip(1).Any(node => node["name"]!.GetValue<string>() != Physical))
            throw new InvalidOperationException("Companion physical type mutation escaped its own insertion");
        owners[0]["name"] = Physical;
    }

    static void NestedPlacementOrder()
    {
        // The inner association appears first in source, but its enclosing generic companion must be placed first.
        var root = JsonNode.Parse("""
        {"types":[
          {"name":"Outer.<companion:Named>.Child.<companion:Named>",
           "kotlinCompanion":{"owner":"Outer.Named.Child","name":"Named","visibility":"public"}},
          {"name":"Outer.<companion:Named>.Child","semanticOwner":"Outer.<companion:Named>"},
          {"name":"Outer.<companion:Named>",
           "kotlinCompanion":{"owner":"Outer","name":"Named","visibility":"public"}},
          {"name":"Outer","typeParams":[{"name":"T"}]}],
         "body":[{"k":"companionValue","companionType":{"t":"fqn","name":"Outer.<companion:Named>.Child.<companion:Named>"}}]}
        """)!;
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        if (!local.Associations.Select(a => a.SemanticName).SequenceEqual(new[] {
                "Outer.<companion:Named>", "Outer.<companion:Named>.Child.<companion:Named>" }) ||
            local.Associations[1].PhysicalOwner != "Outer$companion$Named+Child" ||
            root["types"]![1]!["semanticOwner"]!.GetValue<string>() != "Outer$companion$Named" ||
            !JsonNode.DeepEquals(root["body"]![0], Singleton("Outer.<companion:Named>.Child.$Named")))
            throw new InvalidOperationException("Nested companion binding lost association order or exact physical ownership");
    }
}
