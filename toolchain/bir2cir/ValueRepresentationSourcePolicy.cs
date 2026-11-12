using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using System.Text.Json.Nodes;

// Trusted source binding declarations and referenced metadata establish the
// representation rules before any source bound is erased. No DOM declarations
// or mutable physical slots are retained in this source policy.
sealed class ValueRepresentationSourcePolicy
{
    internal IReadOnlyDictionary<string, ValueRepresentationBinding> Bindings { get; }
    internal IReadOnlyDictionary<string, string> Aliases { get; }
    internal IReadOnlyDictionary<string, SupertypeGraph.Def> Hierarchy { get; }
    readonly bool referenceBuild;

    internal ValueRepresentationSourcePolicy(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, bool referenceBuild)
    {
        this.referenceBuild = referenceBuild;
        Hierarchy = SupertypeGraph.CollectSource(roots);
        var bindings = new Dictionary<string, ValueRepresentationBinding>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(refs?.Aliases ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var assembly in refs?.Assemblies ?? Array.Empty<ReferenceAssembly>())
            foreach (var (name, binding) in assembly.DotKt.ValueRepresentations) bindings[name] = binding;

        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            var (name, attributes) = declaration switch {
                BirClassDefinition type => (type.Name.OrDefault(), type.Attrs.OrEmpty()),
                BirInterfaceDefinition type => (type.Name.OrDefault(), type.Attrs.OrEmpty()),
                BirEnumDefinition type => (type.Name.OrDefault(), type.Attrs.OrEmpty()),
                _ => throw new InvalidOperationException("Unaccounted value representation owner: " + declaration.GetType().Name),
            };
            if (name == null) continue;
            BirAttribute Attribute(string identity) => attributes.SingleOrDefault(attribute =>
                attribute.Attr.OrDefault() is TypeNode.Fqn type && type.Name == identity);
            static string Argument(BirAttribute attribute, int index) =>
                attribute.Args.OrEmpty().ElementAtOrDefault(index) is BirConstant { Value.HasValue: true } constant
                    && constant.Value.Value is BirLiteral.TextLiteral text ? text.Value : null;
            var binding = Attribute(ValueRepresentationLowering.TypeAttribute);
            var source = binding == null ? name : Argument(binding, 4) switch { "" => name, var target => target };
            if (Attribute("kotlin.clr.ClrTypeAlias") is { } alias)
            {
                aliases[name] = Argument(alias, 0);
                aliases[source] = Argument(alias, 0);
            }
            if (binding != null)
                bindings[source] = new(Argument(binding, 0), Argument(binding, 1), Argument(binding, 2), Argument(binding, 3));
        }
        Bindings = bindings;
        Aliases = aliases;
    }

    internal BirFileDeclaration EraseBounds(BirFileDeclaration root) => referenceBuild || Bindings.Count == 0 ? root
        : (BirFileDeclaration)SourceConstraintErasure.Apply(root, constraints => {
            var retained = constraints.Where(bound => bound is not TypeNode.Fqn type || !Bindings.ContainsKey(type.Name)).ToArray();
            return (retained, retained.Length != constraints.Length || constraints.Any(type => !type.Equals(AliasFace(type))));
        }, includeEmptyBounds: true);

    internal TypeNode AliasFace(TypeNode type) => type switch {
        TypeNode.Fqn f when Bindings.ContainsKey(f.Name) =>
            BirTypeLowering.PhysicalHead(f.Name, Aliases[f.Name], f.Args?.Select(AliasFace).ToArray()),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(AliasFace).ToArray()),
        TypeNode.Array a => new TypeNode.Array(AliasFace(a.Elem), a.Rank, a.SzArray),
        TypeNode.Nullable n => new TypeNode.Nullable(AliasFace(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(AliasFace(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(AliasFace(b.Of)),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, AliasFace(p.Of)),
        TypeNode.Fn f => f with { Ret = AliasFace(f.Ret), Params = f.Params.Select(AliasFace).ToArray(),
            Recv = f.Recv == null ? null : AliasFace(f.Recv), Ctx = f.Ctx?.Select(AliasFace).ToArray() },
        _ => type,
    };

    internal static void SelfTest()
    {
        var union = new TypeNode.Fqn("Union");
        var sibling = new TypeNode.Fqn("Sibling");
        BirAttribute Attribute(string name, params string[] values) => new() {
            Attr = new TypeNode.Fqn(name), Args = values.Select(value => (IBirAttributeValue)new BirConstant {
                Value = new BirLiteral.TextLiteral(value),
            }).ToArray(),
        };
        var root = new BirFileDeclaration {
            Types = new IBirTypeDeclaration[] { new BirInterfaceDefinition {
                Name = "Union", Kind = "interface", Attrs = new[] {
                    Attribute("kotlin.clr.ClrTypeAlias", "System.Object"),
                    Attribute(ValueRepresentationLowering.TypeAttribute, "Helpers.matches", "Helpers.classifier", "Helpers.component", "Slots.Union", ""),
                },
            }, new BirClassDefinition {
                Name = "GenericOwner", Kind = "class", Base = union,
                CapturedTypeParams = new[] { new BirTypeParameter { Name = "O" } },
                TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new[] { union } } },
            } },
            Methods = new[] { new BirFunctionDeclaration {
                TypeParams = new[] {
                    new BirTypeParameter { Name = "T", Constraints = new[] { union, sibling } },
                    new BirTypeParameter { Name = "U", Constraints = Array.Empty<TypeNode>() },
                    new BirTypeParameter { Name = "V", Constraints = OptionalField<TypeNode[]>.Null },
                    new BirTypeParameter { Name = "W" },
                    new BirTypeParameter { Name = "N", Constraints = new TypeNode[] { new TypeNode.Array(union) } },
                },
                MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                    [0] = new[] { new TypeNode.Fqn("kotlin.Comparable"), union, sibling },
                }),
            } },
        };
        var before = root.ToDom();
        var policy = new ValueRepresentationSourcePolicy(new[] { root }, null, false);
        var erased = policy.EraseBounds(root);
        var method = erased.Methods.Value[0];
        var bounds = method.MethodTypeParameterBoundsPre.Value.Bounds;
        if (!JsonNode.DeepEquals(before, root.ToDom())
            || method.TypeParams.Value[0].Constraints.Value.Single() != sibling
            || bounds[0][0] is not TypeNode.Fqn { Name: "kotlin.Comparable" }
            || bounds[1].Length != 0 || bounds.ContainsKey(2) || bounds.ContainsKey(3)
            || bounds[4][0] != new TypeNode.Array(union)
            || method.TypeParams.Value[4].Constraints.Value[0] != new TypeNode.Array(union)
            || ((BirClassDefinition)erased.Types.Value[1]).KotlinSupertypesPre.Value.Bounds[0][0] != union
            || policy.Hierarchy["GenericOwner"].Arity != 2 || policy.Hierarchy["GenericOwner"].Base != union)
            throw new InvalidOperationException("Value source policy lost bounds, presence, source hierarchy or input ownership");
        if (!ReferenceEquals(root, new ValueRepresentationSourcePolicy(new[] { root }, null, true).EraseBounds(root)))
            throw new InvalidOperationException("A reference face erased its source bounds");
        var originalHierarchy = SupertypeGraph.Collect(new[] { root.ToDom() });
        foreach (var (name, shape) in policy.Hierarchy)
            if (shape.Arity != originalHierarchy[name].Arity || shape.Kind != originalHierarchy[name].Kind
                || shape.Base != originalHierarchy[name].Base || !shape.Interfaces.SequenceEqual(originalHierarchy[name].Interfaces))
                throw new InvalidOperationException("Typed source hierarchy disagrees with declaration facts");
        Console.WriteLine("[value source policy] self-test OK (typed rules, least-erased bounds, empty/null slots, complete arity)");
    }
}
