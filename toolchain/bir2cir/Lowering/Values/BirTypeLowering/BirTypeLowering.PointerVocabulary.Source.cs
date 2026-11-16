using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class BirTypeLowering
{
    internal static void ValidatePointerVocabulary(BirFileDeclaration root, string file)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitGenericFrame((role, type) => {
                if (!NullableRepresentationViews.Frozen(role)) PointerValidateType(type, file);
            }, (role, child) => {
                if (!NullableRepresentationViews.Frozen(role)) Walk(child);
            });
        }
        Walk(root);
    }
}
