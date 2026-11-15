using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    internal static void SourceSlotsSelfTest()
    {
        static BirAttribute Attribute(string name, params string[] args) => new() {
            Attr = new TypeNode.Fqn(name), Args = args.Select(value => (IBirAttributeValue)new BirConstant {
                Value = new BirLiteral.TextLiteral(value) }).ToArray() };
        static BirFunctionDeclaration Helper(string name, params TypeNode[] args) => new() {
            Name = name, DeclarationId = "helper-" + name,
            Params = args.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
            Ret = name == "convert" ? new TypeNode.Fqn("kotlin.Int") : name == "matches"
                ? new TypeNode.Fqn("kotlin.Boolean") : KotlinRuntimeTypeWitness.PredicateType };
        (BirFileDeclaration Root, BirClassDefinition Alias, BirClassDefinition Child,
            BirClassDefinition Abstract, BirFunctionDeclaration Placeholder, BirPropertyDeclaration Property,
            ValueRepresentationLowering Values) Fixture(bool reference)
        {
            var member = new BirFunctionDeclaration { Name = "convert", DeclarationId = "convert", Abstract = true,
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Int"),
                Attrs = new[] { Attribute(MemberAttribute, "Helpers.convert", "Slots.convert") } };
            var alias = new BirClassDefinition { Name = "Union", Kind = "class", Methods = new[] { member },
                Attrs = new[] { Attribute("kotlin.clr.ClrTypeAlias", "System.Object"),
                    Attribute(TypeAttribute, "Helpers.matches", "Helpers.classifier", "Helpers.component", "", "") } };
            var target = new BirFunctionDeclaration { Name = "actualBody", ExplicitClrName = "physicalBody", DeclarationId = "child-convert",
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Int"),
                Overrides = new[] { new BirOverride { DeclarationId = "convert" } } };
            var child = new BirClassDefinition { Name = "Child", Kind = "class", Base = new TypeNode.Fqn("Union"), Methods = new[] { target } };
            var placeholder = new BirFunctionDeclaration { Name = "abstractBody", DeclarationId = "abstract-convert", Abstract = true,
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Int"), PropertyAssociation = "property-row",
                Body = Array.Empty<IBirStatement>(), Overrides = new[] { new BirOverride { DeclarationId = "convert" } },
                Attrs = new[] { Attribute(MemberAttribute, "Helpers.convert", "Slots.convert") } };
            var property = new BirPropertyDeclaration { Name = "value", PropertyAssociation = "property-row",
                Type = OptionalField<TypeNode>.Null, Attrs = Array.Empty<BirAttribute>() };
            var abs = new BirClassDefinition { Name = "AbstractChild", Kind = "class", Base = new TypeNode.Fqn("Union"),
                Methods = new[] { placeholder }, Properties = new[] { property } };
            var root = new BirFileDeclaration { FileClass = "Helpers", Types = new IBirTypeDeclaration[] { alias, child, abs },
                Methods = new[] { Helper("matches", new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), new TypeNode.Fqn("kotlin.String")),
                    Helper("classifier", new TypeNode.Fqn("kotlin.String")),
                    Helper("component", new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType")),
                    Helper("convert", new TypeNode.Fqn("kotlin.Any")) } };
            var policy = new ValueRepresentationSourcePolicy(new[] { root }, null, reference);
            return (root, alias, child, abs, placeholder, property,
                CreateSource(new[] { root }, null, reference, policy, CaptureSource(new[] { root }), null));
        }
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Source value slots: " + message); }
        foreach (var reference in new[] { false, true })
        {
            var fixture = Fixture(reference);
            var originalTypes = fixture.Root.Types.Value;
            var originalTarget = fixture.Child.Methods.Value[0];
            var appends = new List<(IBirTypeDeclaration Owner, BirFunctionDeclaration Method)>();
            var retired = 0;
            fixture.Values.SynthesizeSlots(new[] { fixture.Root }, _ => false, (owner, methods, properties) => {
                Require(ReferenceEquals(owner, fixture.Abstract) && methods.Count == 1 && properties.Count == 1
                    && ReferenceEquals(methods[0], fixture.Placeholder) && ReferenceEquals(properties[0], fixture.Property)
                    && owner.Methods.Value.Any(method => ReferenceEquals(method, fixture.Placeholder))
                    && ReferenceEquals(owner.Properties.Value[0], fixture.Property), "retirement did not report actual still-owned records");
                retired++;
            }, (owner, methods) => {
                foreach (var method in methods)
                {
                    Require(ReferenceEquals(owner.Methods.Value[^1], method), "append callback preceded attachment");
                    appends.Add((owner, method));
                }
            });
            var bridge = fixture.Child.Methods.Value[1];
            Require(ReferenceEquals(fixture.Root.Types.Value, originalTypes) && ReferenceEquals(fixture.Child.Methods.Value[0], originalTarget)
                && originalTarget.IndependentAliasSlot.Value && appends.Count == 2 && retired == 1,
                "continuing records, append order or selected override fact changed");
            Require(bridge.Generated.Value && bridge.Virtual.Value && !bridge.Static.Value && bridge.Vis.Value == "protected"
                && !bridge.Override.Value && !bridge.Abstract.Value && !bridge.DeclarationId.IsPresent
                && bridge.ClrInterfaceImpls.Value[0].Member.Value == "convert"
                && bridge.ClrInterfaceImpls.Value[0].Owner.Value == new TypeNode.Fqn("Slots")
                && bridge.Body.Value[0] is BirReturn { Value.Value: BirCallInstance call }
                && call.Method.Value == "physicalBody" && call.DeclarationId.Value == "child-convert"
                && call.Virtual.Value && call.ClrOwnerResolved.Value,
                "exact nominal target, method flags or MethodImpl changed");
            Require(fixture.Alias.Methods.Value.Length == 1 && !fixture.Alias.Interfaces.IsPresent
                && fixture.Abstract.Methods.Value.Length == 1 && fixture.Abstract.Methods.Value[0].Abstract.Value
                && fixture.Abstract.Properties.Value.Length == 0, "alias acquired a slot or placeholder/property survived");
            Require(fixture.Values.members["abstract-convert"].Method.IsSource(fixture.Placeholder),
                "producer replaced or recaptured the live source member ledger");
            Require(fixture.Property.Type.IsNull && fixture.Property.Attrs.HasValue && fixture.Placeholder.Attrs.Value.Length == 1,
                "retirement changed source metadata or optional presence");
        }

        var materialized = Fixture(false);
        var actual = new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>() };
        var cast = new BirCast { Type = new TypeNode.Fqn("Union"), E = OptionalField<IBirExpression>.Of(actual) };
        var method = new BirFunctionDeclaration { Name = "$valueClassifier$0", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirReturn { Value = cast } } };
        materialized.Values.BindMaterializedOperations(method);
        Require(method.Body.Value[0] is BirReturn { Value.Value: BirValueBlock block }
            && block.Stmts.Value[0] is BirVariable variable && variable.Name.Value != "$valueClassifier$0"
            && ReferenceEquals(variable.Init.Value, actual), "standalone operation lost collision avoidance or cloned an actual nested callable");
        var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("Effects"), Method = "receiverOnce",
            Ret = new TypeNode.Fqn("Union"), Args = Array.Empty<IBirExpression>() };
        var adapter = new BirFunctionDeclaration { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
            new BirReturn { Value = new BirCallInstance { DeclarationId = "convert", Recv = receiver,
                Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
                DynRet = new TypeNode.Fqn("kotlin.Long"), Ret = new TypeNode.Fqn("kotlin.Int") } } } };
        materialized.Values.BindMaterializedOperations(adapter);
        Require(((BirReturn)adapter.Body.Value[0]).Value.Value is BirCallStatic adapted
            && adapted.DeclarationId.Value == "helper-convert" && adapted.Args.Value.Length == 1
            && ReferenceEquals(adapted.Args.Value[0], receiver) && adapted.Ret.Value == new TypeNode.Fqn("kotlin.Long"),
            "standalone adapter changed receiver ownership or selected result precedence");
        var tv = new TypeNode.Tv("type", 0);
        var detached = new BirFunctionDeclaration { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
            new BirReturn { Value = new BirCast { Type = tv, E = new BirLocal { Name = "x" }, ReifiedTypeOperand = false } } } };
        materialized.Values.BindMaterializedOperations(detached);
        Require(((BirReturn)detached.Body.Value[0]).Value.Value is BirCast { Type.Value: TypeNode.Tv },
            "detached bridge invented a type-owner bound frame");
        var boundedOperand = new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>() };
        var bounded = new BirFunctionDeclaration { Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = Array.Empty<TypeNode>() } },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                [0] = new TypeNode[] { new TypeNode.Fqn("Union") } }),
            Body = new IBirStatement[] { new BirReturn { Value = new BirCast { Type = new TypeNode.Tv("method", 0),
                ReifiedTypeOperand = false, E = OptionalField<IBirExpression>.Of(boundedOperand) } } } };
        materialized.Values.BindMaterializedOperations(bounded);
        var occurrences = 0;
        bounded.Visit(node => { if (ReferenceEquals(node, boundedOperand)) occurrences++; });
        Require(occurrences == 1 && ((BirReturn)bounded.Body.Value[0]).Value.Value is BirCast outer
            && outer.E.Value is BirCast { E.Value: BirValueBlock },
            "standalone method-bound guard lost its saved frame or cloned its registered operand");
        Console.WriteLine("[source value slots] self-test OK (actual retirements, appends, reference faces, exact targets and detached owning operations)");
    }
}
