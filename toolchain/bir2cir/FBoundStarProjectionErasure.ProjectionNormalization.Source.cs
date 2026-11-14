using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class FBoundStarProjectionErasure
{
    // This is the pre-carrier clock: CloseOpenOwners has consumed the original
    // lexical bounds, while native proofs and existential allocation have not
    // captured declaration types yet. Late DOM rewriting uses the same rule.
    internal static void NormalizeRedundantProjections(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        // Collect/CollectDefinitions both select the first declaration, unlike
        // SupertypeGraph's last-wins view. Only the declaration's own vector is
        // selected here, exactly as at this physical phase's DOM entry.
        var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty())
                if (type.Name.HasValue) declarations.TryAdd(type.Name.Value, type);

        TypeNode.Fqn Normalize(TypeNode.Fqn type)
        {
            if (type.Args is not { Length: > 0 } arguments) return type;
            IReadOnlyList<BirTypeParameter> parameters = declarations.TryGetValue(type.Name, out var local)
                ? local.TypeParams.OrDefault() : refs.OwnerTypeParametersInPhysicalFrame(type.Name);
            if (parameters?.Count != arguments.Length) return type;
            return type with { Args = arguments.Select((argument, index) =>
                NormalizeProjectionArgument(argument, parameters[index]?.Variance.OrDefault())).ToArray() };
        }
        void Rewrite(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitChildren(Rewrite);
            // Generated own slots exclude encoded source facts, semantic
            // signatures, metadata codecs and companionReceiver string facts.
            // Foreign/override descriptors ARE structured types and participate.
            node.RewriteOwnTypeFieldsInPlace((_, _, type) => type == null ? null
                : SourceDeclarationSignature.RewriteType(type, Normalize));
        }
        foreach (var root in roots) Rewrite(root);
    }

    static TypeNode NormalizeProjectionArgument(TypeNode argument, string declarationVariance) =>
        argument is TypeNode.Projection projection && declarationVariance == projection.Variance ? projection.Of : argument;
}
