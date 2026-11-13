using DotKt.Bir.Lowering;

// Applied attributes are owning graph occurrences regardless of their carrier
// (assembly, return, declaration, or parameter). Saved executable payloads are
// metadata, not further live declarations for this physical binding pass.
static partial class TypedAttributeBinding
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex references, string file)
    {
        AttrExternalNormalize.Apply(root);
        ClrMemberResolution.ResolveAttributeCtors(root, references, file);
    }

    internal static void Visit(BirGraphValue root, Action<BirAttribute> visit)
    {
        if (root is null or IBirExecutablePayload) return;
        if (root is BirAttribute attribute) visit(attribute);
        root.VisitChildren(child => Visit(child, visit));
    }
}
