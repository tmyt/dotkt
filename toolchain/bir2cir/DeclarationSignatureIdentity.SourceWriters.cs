using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    internal static void RecordSelectedImplementationSlot(BirFunctionDeclaration implementation, BirFunctionDeclaration slot)
    {
        if (!slot.DeclarationId.HasValue) return;
        var existing = implementation.SignatureOverrideDeclarations.OrEmpty();
        if (!existing.Contains(slot.DeclarationId.Value, StringComparer.Ordinal))
            implementation.SignatureOverrideDeclarations = existing.Append(slot.DeclarationId.Value).ToArray();
    }

    internal static void RewriteParameterFacts(BirParameter parameter, Func<TypeNode, TypeNode> rewrite)
    {
        if (parameter.NominalParameterSignatureIdentity.HasValue)
            parameter.NominalParameterSignatureIdentity = rewrite(parameter.NominalParameterSignatureIdentity.Value);
        if (parameter.GenericParameterSignatureBounds.HasValue)
            parameter.GenericParameterSignatureBounds = parameter.GenericParameterSignatureBounds.Value.Select(rewrite).ToArray();
    }

    static void SourceWritersSelfTest()
    {
        var implementation = new BirFunctionDeclaration { SignatureOverrideDeclarations = new[] { "previous" } };
        var slot = new BirFunctionDeclaration { DeclarationId = "selected" };
        RecordSelectedImplementationSlot(implementation, slot);
        var once = implementation.SignatureOverrideDeclarations.Value;
        RecordSelectedImplementationSlot(implementation, slot);
        RecordSelectedImplementationSlot(implementation, new BirFunctionDeclaration());
        if (!once.SequenceEqual(new[] { "previous", "selected" })
            || !ReferenceEquals(once, implementation.SignatureOverrideDeclarations.Value))
            throw new InvalidOperationException("Owning selected-slot writer lost identity, order or first admission");
        var variable = new TypeNode.Tv("type", 0);
        var replacement = new TypeNode.Fqn("System.String");
        var parameter = new BirParameter { NominalParameterSignatureIdentity = variable,
            GenericParameterSignatureBounds = new TypeNode[] { variable } };
        TypeNode Rewrite(TypeNode type) => type == variable ? replacement : type;
        RewriteParameterFacts(parameter, Rewrite);
        if (parameter.NominalParameterSignatureIdentity.Value != replacement
            || parameter.GenericParameterSignatureBounds.Value.Single() != replacement)
            throw new InvalidOperationException("Owning signature fact writer lost its selected type facts");
        var absent = new BirParameter { GenericParameterSignatureBounds = OptionalField<TypeNode[]>.Null };
        RewriteParameterFacts(absent, Rewrite);
        if (absent.NominalParameterSignatureIdentity.IsPresent || !absent.GenericParameterSignatureBounds.IsNull)
            throw new InvalidOperationException("Owning signature fact writer changed absent/null facts");
    }
}
