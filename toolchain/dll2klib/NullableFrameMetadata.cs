using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

internal static class NullableFrameMetadata
{
    public static NullableRepresentationFrame? TypeFrame(MetadataReader reader, MetadataAttributes attributes,
        TypeDefinitionHandle handle)
    {
        if (handle.IsNil) return null;
        using var document = attributes.CarrierDocument(handle, MetadataAttributes.DotKtNs + "KotlinSupertypesAttribute");
        return Read(document, reader.GetTypeDefinition(handle).GetGenericParameters().Count);
    }

    public static NullableRepresentationFrame? MethodFrame(MetadataReader reader, MetadataAttributes attributes,
        MethodDefinitionHandle handle)
    {
        if (handle.IsNil) return null;
        using var document = attributes.CarrierDocument(handle, MetadataAttributes.DotKtNs + "KotlinDeclarationIdentityAttribute");
        var frame = Read(document, reader.GetMethodDefinition(handle).GetGenericParameters().Count);
        if (frame is not null && !document!.RootElement.TryGetProperty("signature", out _))
            throw new InvalidDataException("Nullable representation frame requires original Kotlin signature");
        return frame;
    }

    private static NullableRepresentationFrame? Read(JsonDocument? document, int physicalArity)
    {
        if (document is null || !document.RootElement.TryGetProperty(NullableRepresentationFrame.MetadataKey, out var node))
            return null;
        var frame = NullableRepresentationFrame.Read(JsonNode.Parse(node.GetRawText())!);
        if (frame.PhysicalArity != physicalArity)
            throw new InvalidDataException("Nullable representation frame disagrees with declared CLR generic arity");
        return frame;
    }
}
