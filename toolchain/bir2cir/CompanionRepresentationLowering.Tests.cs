using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static class CompanionRepresentationLoweringTests
{
    const string Semantic = "Probe.Owner.<companion:Named>";
    const string Physical = "Probe.Owner.$Named";

    public static void SelfTest()
    {
        var owner = new BirClassDefinition { Kind = "class", Name = "Probe.Owner" };
        var companion = new BirClassDefinition { Kind = "class", Name = Semantic,
            KotlinCompanion = new BirKotlinCompanion { Owner = "Probe.Owner", Name = "Named", Visibility = "public" } };
        var generic = new BirClassDefinition { Kind = "class", Name = "Probe.Generic",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var genericCompanion = new BirClassDefinition { Kind = "class", Name = "Probe.Generic.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Probe.Generic", Name = "Named", Visibility = "internal" } };
        var child = new BirClassDefinition { Kind = "class", Name = "Probe.Child", SemanticOwner = Semantic, Vis = "private" };
        BirCompanionValue Companion() => new() { CompanionType = new TypeNode.Fqn(Semantic) };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn(Semantic), Recv = Companion(),
            Method = "selected", DeclarationId = "selected-id", Sig = new TypeNode[] { new TypeNode.Fqn("Probe.Parameter") } };
        var bound = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn(Semantic), CalleeOwner = new TypeNode.Fqn("Probe.Sentinel"), Recv = Companion() };
        var inherited = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn("Probe.SelectedOwner"), CalleeOwner = new TypeNode.Fqn("Probe.SelectedOwner"), Recv = Companion() };
        var ordinary = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn(Semantic), CalleeOwner = new TypeNode.Fqn(Semantic), Recv = new BirLocal { Name = "ordinary" } };
        var field = new BirFieldDeclaration { Name = "slot", Type = new TypeNode.Nullable(new TypeNode.Fqn(Semantic)) };
        var root = new BirFileDeclaration { FileClass = "Probe.File",
            Types = new IBirTypeDeclaration[] { owner, companion, generic, genericCompanion, child },
            Fields = new[] { field }, Methods = new[] { new BirFunctionDeclaration { Name = "use",
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = call },
                    new BirExpressionStatement { Expr = bound }, new BirExpressionStatement { Expr = inherited },
                    new BirExpressionStatement { Expr = ordinary } },
            } } };
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        if (!ReferenceEquals(root.Types.Value[1], companion) || companion.Name.Value != Physical
            || companion.NestedIn.Value != "Probe.Owner" || genericCompanion.NestedIn.IsPresent
            || genericCompanion.Name.Value != "Probe.Generic$companion$Named"
            || companion.Fields.Value[0].Name.Value != "$INSTANCE"
            || owner.Fields.Value.Single().Name.Value != "Named"
            || genericCompanion.CompanionCarrier.Value.PhysicalOwnerArity.Value != 1
            || child.SemanticOwner.Value != Physical || child.Vis.Value != "internal"
            || field.Type.Value != new TypeNode.Nullable(new TypeNode.Fqn(Physical))
            || call.OwnerType.Value != new TypeNode.Fqn(Physical) || call.DeclarationId.Value != "selected-id"
            || call.Sig.Value.Single() != new TypeNode.Fqn("Probe.Parameter")
            || call.Recv.Value is not BirStaticFieldGet { Name.Value: "$INSTANCE", Sty.Value: TypeNode.Fqn { Name: Physical } }
            || bound.CalleeOwner.Value != new TypeNode.Fqn(Physical)
            || inherited.OwnerType.Value != new TypeNode.Fqn("Probe.SelectedOwner")
            || ordinary.OwnerType.Value != new TypeNode.Fqn(Physical) || ordinary.CalleeOwner.Value != new TypeNode.Fqn(Physical))
            throw new InvalidOperationException("Typed companion placement lost exact declaration/use/visibility facts");

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
        var inner = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>.Child.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Outer.Named.Child", Name = "Named", Visibility = "public" } };
        var child = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>.Child", SemanticOwner = "Outer.<companion:Named>" };
        var outerCompanion = new BirClassDefinition { Kind = "class", Name = "Outer.<companion:Named>",
            KotlinCompanion = new BirKotlinCompanion { Owner = "Outer", Name = "Named", Visibility = "public" } };
        var outer = new BirClassDefinition { Kind = "class", Name = "Outer", TypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        var value = new BirExpressionStatement { Expr = new BirCompanionValue { CompanionType = new TypeNode.Fqn(inner.Name.Value) } };
        var root = new BirFileDeclaration { FileClass = "Nested", Types = new IBirTypeDeclaration[] { inner, child, outerCompanion, outer },
            Methods = new[] { new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { value } } } };
        var local = CompanionRepresentationLowering.Apply(new[] { root });
        if (!local.Associations.Select(a => a.SemanticName).SequenceEqual(new[] {
                "Outer.<companion:Named>", "Outer.<companion:Named>.Child.<companion:Named>" })
            || local.Associations[1].PhysicalOwner != "Outer$companion$Named+Child"
            || child.SemanticOwner.Value != "Outer$companion$Named"
            || value.Expr.Value is not BirStaticFieldGet { OwnerType.Value: TypeNode.Fqn { Name: "Outer.<companion:Named>.Child.$Named" } })
            throw new InvalidOperationException("Nested companion binding lost association order or exact physical ownership");
    }
}
