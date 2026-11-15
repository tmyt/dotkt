using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // Grant only the current declaration occurrences at the metadata seam.
    // CompleteMetadata remains the sole interpreter/encoder of these facts.
    internal static void PrepareMetadata(BirFileDeclaration file, bool runtimeMode,
        Action<BirFileDeclaration, IBirTypeDeclaration, BirFunctionDeclaration,
            BirRoundtripDeclarationIdentity> reportIdentityTransfer)
    {
        ArgumentNullException.ThrowIfNull(reportIdentityTransfer);
        file.RoundtripMetadataPending = true;
        Methods(file.Methods.OrEmpty(), null);
        Fields(file.Fields.OrEmpty());
        Properties(file.Properties.OrEmpty());
        foreach (var type in file.Types.OrEmpty())
        {
            PrepareHeader(type, runtimeMode);
            if (!runtimeMode && type.Kind.OrDefault() == "enum") continue;
            Methods(type.Methods.OrEmpty(), type);
            Fields(type.Fields.OrEmpty());
            Properties(type.Properties.OrEmpty());
            foreach (var constructor in type.Ctors.OrEmpty())
            {
                constructor.RoundtripMetadataPending = true;
                // Constructor declarations own these three identity fields, not
                // a method-generic frame or reified-method parameter vectors.
                var identity = new BirRoundtripDeclarationIdentity {
                    DeclarationId = constructor.DeclarationId,
                    DeclarationSourceName = constructor.DeclarationSourceName,
                    DeclarationSignatureAllocation = constructor.DeclarationSignatureAllocation,
                };
                if (IdentityPresent(identity)) constructor.RoundtripDeclarationIdentity = identity;
                constructor.DeclarationId = default;
                constructor.DeclarationSourceName = default;
                constructor.DeclarationSignatureAllocation = default;
                Parameters(constructor.Params.OrEmpty());
            }
        }

        void Methods(BirFunctionDeclaration[] methods, IBirTypeDeclaration owner)
        {
            foreach (var method in methods)
            {
                method.RoundtripMetadataPending = true;
                var identity = new BirRoundtripDeclarationIdentity {
                    DeclarationId = method.DeclarationId,
                    DeclarationSourceName = method.DeclarationSourceName,
                    DeclarationSignatureAllocation = method.DeclarationSignatureAllocation,
                    DeclarationSemanticSignature = method.DeclarationSemanticSignature,
                    SemanticReifiedIndices = method.SemanticReifiedIndices,
                    NullableWitnessIndices = method.NullableWitnessIndices,
                    NullableRepresentationFrame = method.NullableRepresentationFrame,
                };
                if (IdentityPresent(identity)) method.RoundtripDeclarationIdentity = identity;
                // Custody is reported on the actual attached target while the
                // original raw ID is still available to the strict ledger.
                if (method.DeclarationId.HasValue) reportIdentityTransfer(file, owner, method, identity);
                method.DeclarationId = default;
                method.DeclarationSourceName = default;
                method.DeclarationSignatureAllocation = default;
                method.DeclarationSemanticSignature = default;
                method.SemanticReifiedIndices = default;
                method.NullableWitnessIndices = default;
                method.NullableRepresentationFrame = default;
                if (runtimeMode) method.MethodTypeParameterBoundsPre = default;
                else if (method.MethodTypeParameterBoundsPre.IsPresent)
                {
                    method.RoundtripMethodBounds = method.MethodTypeParameterBoundsPre;
                    method.MethodTypeParameterBoundsPre = default;
                }
                Parameters(method.Params.OrEmpty());
            }
        }
        static void Fields(BirFieldDeclaration[] fields)
        { foreach (var field in fields) field.RoundtripMetadataPending = true; }
        static void Properties(BirPropertyDeclaration[] properties)
        { foreach (var property in properties) property.RoundtripMetadataPending = true; }
        static void Parameters(BirParameter[] parameters)
        { foreach (var parameter in parameters) parameter.RoundtripMetadataPending = true; }
    }

    static bool IdentityPresent(BirRoundtripDeclarationIdentity identity) => identity.DeclarationId.IsPresent
        || identity.DeclarationSourceName.IsPresent || identity.DeclarationSignatureAllocation.IsPresent
        || identity.DeclarationSemanticSignature.IsPresent || identity.SemanticReifiedIndices.IsPresent
        || identity.NullableWitnessIndices.IsPresent || identity.NullableRepresentationFrame.IsPresent;

    static void PrepareHeader(IBirTypeDeclaration type, bool runtimeMode)
    {
        switch (type)
        {
            case BirClassDefinition value:
                value.RoundtripTypeHeaderPending = true;
                if (runtimeMode) value.KotlinSupertypesPre = default;
                else if (type.Kind.OrDefault() != "enum" && value.KotlinSupertypesPre.IsPresent)
                { value.RoundtripSupertypesPre = value.KotlinSupertypesPre; value.KotlinSupertypesPre = default; }
                break;
            case BirInterfaceDefinition value:
                value.RoundtripTypeHeaderPending = true;
                if (runtimeMode) value.KotlinSupertypesPre = default;
                else if (type.Kind.OrDefault() != "enum" && value.KotlinSupertypesPre.IsPresent)
                { value.RoundtripSupertypesPre = value.KotlinSupertypesPre; value.KotlinSupertypesPre = default; }
                break;
            case BirEnumDefinition value:
                value.RoundtripTypeHeaderPending = true;
                if (runtimeMode) value.KotlinSupertypesPre = default;
                break;
        }
    }
}
