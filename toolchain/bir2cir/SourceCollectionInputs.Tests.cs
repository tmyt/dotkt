using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class SourceCollectionInputs
{
    internal static void SelfTest()
    {
        var slot = new TypeNode.Tv("method", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        var helper = new BirFunctionDeclaration {
            Name = "contentToString", Static = true, DeclarationId = "array-content",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] { new BirParameter { Name = "value",
                Type = new TypeNode.Nullable(new TypeNode.Array(new TypeNode.Projection("out", slot))) } },
        };
        var iterator = new BirFunctionDeclaration { Name = "iterator", Params = Array.Empty<BirParameter>() };
        var inner = new BirClassDefinition {
            Name = "probe.Inner", Kind = "class", SemanticOwner = "probe.Outer", OuterTypeParamCount = 1,
            Mods = new BirModifiers { Inner = true }, Methods = new[] { iterator },
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "O" } },
            TypeParams = new[] { new BirTypeParameter { Name = "I" } },
            Interfaces = new TypeNode[] { new TypeNode.Fqn("probe.Contract", new TypeNode[] { new TypeNode.Tv("type", 1) }) },
        };
        var property = new BirClassDefinition { Name = "probe.Property", Kind = "class",
            Methods = new[] { iterator with { PropertyName = "iterator", PropertyAccessor = "get" } } };
        var root = new BirFileDeclaration {
            FileClass = "kotlin.collections._ArraysKt", Methods = new[] { helper },
            Types = new IBirTypeDeclaration[] { inner, property },
        };
        // Fixture-only DOM adaptation checks both fact readers against the same
        // declarations; production transfers the owning graph only once later.
        var typed = new SourceCollectionInputs(new[] { root }, null);
        var dom = root.ToDom();
        var domApplications = new InnerApplicationOrder(new JsonNode[] { dom }, null);
        var domIterators = MemberCallSubstitution.CollectDeclaredIteratorTypes(dom);
        var domDefinitions = SupertypeGraph.Collect(new JsonNode[] { dom });
        var sourceArgs = new TypeNode[] { new TypeNode.Star() };
        var call = new BirCallStatic {
            Owner = new TypeNode.Fqn("kotlin.collections._ArraysKt"), Method = "contentToString",
            Sig = new TypeNode[] { new TypeNode.Nullable(new TypeNode.Array(new TypeNode.Projection("out", slot))) },
            TypeArgs = sourceArgs, Args = Array.Empty<IBirExpression>(),
        };
        {
            var selected = typed.Helpers.Bind(call);
            var applied = new TypeNode.Fqn("probe.Inner", new TypeNode[] { slot, text });
            var application = typed.Applications.DeclarationArguments(applied);
            if (selected.DeclarationId.OrDefault() != "array-content"
                || selected.TypeArgs.Value[0] != new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))
                || !ReferenceEquals(selected.Args.Value, call.Args.Value)
                || application[0] != text || application[1] != slot
                || !application.SequenceEqual(domApplications.DeclarationArguments(applied))
                || !typed.DeclaredIterators.SetEquals(new[] { "probe.Inner" }) || !typed.DeclaredIterators.SetEquals(domIterators)
                || typed.Definitions["probe.Inner"].Arity != 2 || typed.Definitions["probe.Inner"].Arity != domDefinitions["probe.Inner"].Arity
                || typed.Definitions["probe.Inner"].Interfaces.Single().Name != "probe.Contract"
                || !typed.Definitions["probe.Inner"].Interfaces.SequenceEqual(domDefinitions["probe.Inner"].Interfaces))
                throw new InvalidOperationException("Source collection fact capture disagrees across the owning frontier");
        }
        helper.DeclarationId = "changed-after-capture";
        inner.Mods.Value.Inner = false;
        inner.Methods = Array.Empty<BirFunctionDeclaration>();
        if (typed.Helpers.Bind(call).DeclarationId.OrDefault() != "array-content"
            || !typed.DeclaredIterators.Contains("probe.Inner")
            || typed.Applications.DeclarationArguments(new TypeNode.Fqn("probe.Inner", new TypeNode[] { slot, text }))[0] != text)
            throw new InvalidOperationException("Source collection inputs retained mutable declaration occurrences");

        foreach (var element in new TypeNode[] { text, new TypeNode.Star(), new TypeNode.Projection("in", text), new TypeNode.Projection("out", text) })
        {
            var operand = new BirLocal { Name = "array", Sty = new TypeNode.Array(element) };
            var intrinsic = new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.internal.ir"),
                Method = "dataClassArrayMemberToString", Args = new IBirExpression[] { operand } };
            var lowered = (BirCallStatic)DataClassArrayMemberLowering.Apply(intrinsic, typed.Helpers.Bind);
            var expected = element is TypeNode.Star or TypeNode.Projection { Variance: "in" }
                ? new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))
                : element is TypeNode.Projection projection ? projection.Of : element;
            if (lowered.DeclarationId.OrDefault() != "array-content" || lowered.TypeArgs.Value[0] != expected
                || !ReferenceEquals(lowered.Args.Value[0], operand))
                throw new InvalidOperationException("Owning array content helper lost source variance or operand ownership");
        }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var starList = new TypeNode.Fqn("System.Collections.Generic.List", new TypeNode[] { new TypeNode.Star() });
        var exactList = new TypeNode.Fqn(starList.Name, new TypeNode[] { text });
        var locals = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "closedViews", Body = new IBirStatement[] {
                // A forward alias forces the common fixed point to run again.
                new BirVariable { Name = "alias", Type = starList, Init = new BirLocal { Name = "exact" } },
                new BirVariable { Name = "exact", Type = starList,
                    Init = new BirCast { Type = exactList, E = new BirLocal { Name = "input" } } },
            },
        } } };
        var typedHints = ForeignStarProjectionBinding.CollectClosedViewHints(new[] { locals }, refs);
        var domHints = ForeignStarProjectionBinding.CollectClosedViewHints(new JsonNode[] { locals.ToDom() }, refs);
        if (typedHints.Count != 2 || domHints.Count != 2 || typedHints["alias"] != exactList
            || typedHints.Any(pair => !domHints.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidOperationException("Owning closed-view capture lost an exact native view or alias fixed point");
        Console.WriteLine("[source collection inputs] self-test OK (fact parity, capture clock, source array projections, native closed views)");
    }
}
