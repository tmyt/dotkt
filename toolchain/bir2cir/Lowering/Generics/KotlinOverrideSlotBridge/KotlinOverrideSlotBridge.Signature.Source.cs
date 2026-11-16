using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    internal static BirFunctionDeclaration BuildSignatureBridgeSource(IBirTypeDeclaration owner,
        BirFunctionDeclaration implementation, TypeNode[] signature, TypeNode result, string name,
        ValueTypeOracle isValue, ReferenceMetadataIndex refs)
    {
        // Only the value view of the header changes. The source body and every
        // registered descendant stay owned by the original declaration.
        var header = SelectedMethodHeader.FromSource(implementation);
        var declarationSignature = implementation.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();
        header.Params = header.Params.Value.Select(parameter => {
            var value = parameter.Copy();
            value.Type = SignatureValueTypes.Of(parameter.Type.OrDefault());
            return value;
        }).ToArray();
        var bridge = BuildBridgeSource(owner, header, signature.Select(SignatureValueTypes.Of).ToArray(),
            result, name, isValue, refs, declarationSignature: declarationSignature);
        for (var index = 0; index < signature.Length; index++) bridge.Params.Value[index].Type = BirGraphValue.CopyType(signature[index]);
        return bridge;
    }
}
