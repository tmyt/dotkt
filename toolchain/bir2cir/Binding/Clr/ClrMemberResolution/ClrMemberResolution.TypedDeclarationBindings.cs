using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    // These declaration-only edits run before CGI can append callable adapters.
    // They preserve the exact continuing method/type vectors and signatures.
    internal static void ApplyDeclarationBindings(BirFileDeclaration document,
        ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes)
    {
        _localTypes = localTypes ?? new HashSet<string>();
        ResolveExternalClassOverrides(document, refs);
        ResolveExternalBaseMethodImpls(refs, document, _localTypes);
    }
}
