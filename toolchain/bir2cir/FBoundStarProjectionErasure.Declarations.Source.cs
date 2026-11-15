using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FBoundStarProjectionErasure
{
    internal sealed record GenericOwner(string Name, CurrentDeclarationBindings.DeclarationBinding Definition,
        CurrentDeclarationBindings.DeclarationBinding Root, int Arity, bool Needed);

    internal sealed class CurrentDeclarationPreparation
    {
        internal CurrentDeclarationBindings Bindings { get; }
        internal IReadOnlyList<GenericOwner> GenericOwners { get; }
        internal IReadOnlyDictionary<string, string> LocalClrAliases { get; }
        internal IReadOnlyDictionary<string, SupertypeGraph.Def> SourceSupertypes => Bindings.SourceSupertypes;

        internal CurrentDeclarationPreparation(CurrentDeclarationBindings bindings)
        {
            Bindings = bindings;
            LocalClrAliases = CollectLocalClrAliases(bindings);
            var generic = new Dictionary<string, GenericOwner>(StringComparer.Ordinal);
            foreach (var rootBinding in bindings.SourceRoots)
                foreach (var type in ((BirFileDeclaration)rootBinding.Source).Types.OrEmpty())
                {
                    if (!type.Name.HasValue) continue;
                    var name = type.Name.Value;
                    var binding = bindings.GetSource((BirGraphValue)type);
                    var alias = type.Attrs.OrEmpty().Any(attribute =>
                        attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" });
                    var arity = type.TypeParams.OrEmpty().Length;
                    if (IsSourceGeneric(name, type.Generated.OrDefault(), alias, arity))
                        generic.TryAdd(name, new GenericOwner(name, binding, rootBinding, arity,
                            type.Vis.OrDefault() is null or "public" or "protected" or "internal"));
                }
            GenericOwners = generic.Values.ToArray();
        }
    }

    internal static CurrentDeclarationPreparation PrepareDeclarationContinuation(CurrentDeclarationBindings bindings) => new(bindings);

    internal static CurrentDeclarationBindings PrepareCurrentDeclarations(IReadOnlyList<BirFileDeclaration> roots,
        GenericRepresentationPolicy values)
        => PrepareCurrentDeclarations(roots, values, out _);

    internal static CurrentDeclarationBindings PrepareCurrentDeclarations(IReadOnlyList<BirFileDeclaration> roots,
        GenericRepresentationPolicy values, out CurrentDeclarationPreparation preparation)
    {
        var declarations = new CurrentDeclarationBindings(roots);
        preparation = PrepareDeclarationContinuation(declarations);
        values.RegisterCurrentPhysicalOwners(declarations.SourceDefinitions.Keys, preparation.LocalClrAliases);
        return declarations;
    }

    static IReadOnlyDictionary<string, string> CollectLocalClrAliases(CurrentDeclarationBindings declarations)
        => CollectLocalClrAliases(declarations.SourceDefinitions.Select(pair =>
            new KeyValuePair<string, IBirTypeDeclaration>(pair.Key, (IBirTypeDeclaration)pair.Value.Source)));

    static IReadOnlyDictionary<string, string> CollectLocalClrAliases(
        IEnumerable<KeyValuePair<string, IBirTypeDeclaration>> definitions)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, definition) in definitions)
        foreach (var attribute in definition.Attrs.OrEmpty())
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
