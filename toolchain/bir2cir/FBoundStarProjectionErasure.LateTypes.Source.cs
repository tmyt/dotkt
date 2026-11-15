using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DeclarationBinding = CurrentDeclarationBindings.DeclarationBinding;

static partial class FBoundStarProjectionErasure
{
    // A pass-local physical inventory, not a new source declaration catalog.
    // Every occurrence must have been admitted by its actual producer already.
    internal sealed class CurrentPhysicalTypeView
    {
        internal IReadOnlyDictionary<string, IBirTypeDeclaration> Definitions { get; }
        internal IReadOnlyDictionary<string, DeclarationBinding> Bindings { get; }
        internal IReadOnlyDictionary<string, string> LocalClrAliases { get; }

        // The same exact bindings change addresses once; no current graph is
        // rediscovered after the source side has been consumed.
        internal Dictionary<string, System.Text.Json.Nodes.JsonObject> BindDefinitions() => Bindings
            .ToDictionary(pair => pair.Key, pair => pair.Value.Physical, StringComparer.Ordinal);

        CurrentPhysicalTypeView(Dictionary<string, IBirTypeDeclaration> definitions,
            Dictionary<string, DeclarationBinding> bindings, IReadOnlyDictionary<string, string> aliases)
        { Definitions = definitions; Bindings = bindings; LocalClrAliases = aliases; }

        internal static CurrentPhysicalTypeView Capture(IReadOnlyList<BirFileDeclaration> roots,
            CurrentDeclarationBindings declarations)
        {
            var definitions = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
            var bindings = new Dictionary<string, DeclarationBinding>(StringComparer.Ordinal);
            foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
            {
                var binding = declarations.GetCapturedSource((BirGraphValue)type);
                if (type.Name.HasValue && definitions.TryAdd(type.Name.Value, type))
                    bindings.Add(type.Name.Value, binding);
            }
            return new(definitions, bindings, CollectLocalClrAliases(definitions));
        }
    }

    internal static CurrentPhysicalTypeView RewriteLateTypesSource(IReadOnlyList<BirFileDeclaration> roots,
        CarrierRealizationContext realization, AliasDeclarationTransitions aliases = null)
    {
        var current = RefreshCurrentPhysicalTypesSource(roots, realization);
        var projection = CarrierProjectionContext.ForCurrentSource(realization, current, aliases);
        var state = new SourceRewriteState(realization, (_, _) => null, current, projection);
        NormalizeRedundantProjections(roots, realization.References);
        foreach (var root in roots)
            RewriteSource(new BirGraphSlot(() => root, replacement => {
                if (!ReferenceEquals(replacement, root))
                    throw new InvalidOperationException("Late carrier projection replaced a file declaration");
            }), new(), state, null, typesOnly: true);
        return current;
    }

    // Late producers report actual new declarations before projecting them.
    // Refresh only physical membership here; never replay the original graph's
    // type rewrites or reselect source eligibility after a materialization.
    internal static CurrentPhysicalTypeView RefreshCurrentPhysicalTypesSource(
        IReadOnlyList<BirFileDeclaration> roots, CarrierRealizationContext realization)
    {
        var current = CurrentPhysicalTypeView.Capture(roots, realization.Bindings);
        realization.Values.RegisterCurrentPhysicalOwners(current.Definitions.Keys, current.LocalClrAliases);
        realization.NativeProofs.RegisterCurrentSourceDefinitions(current);
        return current;
    }
}
