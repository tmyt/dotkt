using DotKt.Bir.Lowering;

static partial class ClrMemberResolution
{
    public static void CheckStamped(BirFileDeclaration root, string file) => root.Visit(node => {
        switch (node)
        {
            case BirCallStatic value: value.DotktDeclarationIdentityBound = default; break;
            case BirCallInstance value: value.DotktDeclarationIdentityBound = default; break;
            case BirNewDelegate value: value.DotktDeclarationIdentityBound = default; break;
            case BirNewBoundDelegate value: value.DotktDeclarationIdentityBound = default; break;
            case BirConstrainedCall value: value.DotktDeclarationIdentityBound = default; break;
        }
        if (ForeignDeclarationFacts.Read(node) is { } facts)
            RequireForeignDeclarationStamp(facts.Kind, facts.HasParameterStamp, facts.MemberRef != null, facts.DeclaredReturn != null, file);
    });
}
