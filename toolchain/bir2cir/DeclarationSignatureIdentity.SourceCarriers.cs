using DotKt.Bir.Lowering;

static partial class DeclarationSignatureIdentity
{
    internal static void RemoveCapturedFacts(BirGraphValue node)
    {
        switch (node)
        {
            case BirParameter parameter:
                parameter.GenericParameterSignatureBounds = default;
                parameter.NominalParameterSignatureIdentity = default;
                break;
            case BirLambdaParameter parameter:
                parameter.GenericParameterSignatureBounds = default;
                parameter.NominalParameterSignatureIdentity = default;
                break;
            case BirFunctionDeclaration method: method.SignatureOverrideDeclarations = default; break;
            case BirInheritedDefaultAccessor accessor: accessor.SignatureOverrideDeclarations = default; break;
            case BirInheritedDefaultMethod method: method.SignatureOverrideDeclarations = default; break;
            case BirInheritedClassMethod method: method.SignatureOverrideDeclarations = default; break;
        }
        // Allocation belongs to a later DOM physical phase and cannot exist on
        // this source profile. Opaque carrier bodies remain untouched here.
        node.VisitChildren(RemoveCapturedFacts);
    }
}
