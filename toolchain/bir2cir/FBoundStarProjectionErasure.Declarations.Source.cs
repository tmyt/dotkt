using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal static CurrentDeclarationBindings PrepareCurrentDeclarations(IReadOnlyList<BirFileDeclaration> roots,
        GenericRepresentationPolicy values)
    {
        var declarations = new CurrentDeclarationBindings(roots);
        values.RegisterCurrentPhysicalOwners(declarations.SourceDefinitions.Keys, CollectLocalClrAliases(declarations));
        return declarations;
    }

    static IReadOnlyDictionary<string, string> CollectLocalClrAliases(CurrentDeclarationBindings declarations)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, binding) in declarations.SourceDefinitions)
        foreach (var attribute in ((IBirTypeDeclaration)binding.Source).Attrs.OrEmpty())
        {
            if (attribute.Attr.OrDefault() is not TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }) continue;
            var target = attribute.Args.OrDefault() is [BirConstant { Value.HasValue: true, Value.Value: BirLiteral.TextLiteral text }]
                ? text.Value : null;
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException($"bir2cir: malformed @ClrTypeAlias on '{name}'");
            aliases[name] = target;
        }
        return aliases;
    }
}
