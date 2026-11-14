using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    internal static byte[] VisibilityContent(string visibility) => BirCarrier.EncodeBody(BirCarrier.JsonV1, visibility,
        (writer, value) => { writer.WriteStartObject(); writer.WriteString("visibility", value); writer.WriteEndObject(); });

    static byte[] PropertyStorageContent(string owner, string field) => BirCarrier.EncodeBody(
        BirCarrier.JsonV1, (Owner: owner, Field: field), (writer, value) => {
            writer.WriteStartObject(); writer.WriteString("owner", value.Owner); writer.WriteString("field", value.Field); writer.WriteEndObject();
        });

    static byte[] ExtensionCoreContent(string name) => BirCarrier.EncodeBody(BirCarrier.JsonV1, name,
        (writer, value) => { writer.WriteStartObject(); writer.WriteString("name", value); writer.WriteEndObject(); });

    static byte[] SourceMethodIdentityContent(string name, string declaration) => BirCarrier.EncodeBody(
        BirCarrier.JsonV1, (Name: name, Declaration: declaration), (writer, value) => {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            if (value.Declaration != null) writer.WriteString("sourceDeclaration", value.Declaration);
            writer.WriteEndObject();
        });

    static BirAttribute PlacementCarrier(string name, byte[] content) => new() {
        Attr = new TypeNode.Fqn(name),
        ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
        Args = new IBirAttributeValue[] {
            new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(BirCarrier.JsonV1) },
            new BirOpaqueBytes { Bytes = content },
        },
    };

    public static void PreserveTypeVisibility(IBirTypeDeclaration type) => type.Attrs = type.Attrs.OrEmpty()
        .Append(PlacementCarrier(AKTypeVisibility, VisibilityContent(type.Vis.Value))).ToArray();

    internal static void StampPropertyStorage(BirFunctionDeclaration getter, string owner, string field)
        => getter.Attrs = getter.Attrs.OrEmpty().Append(PlacementCarrier(AKPropertyStorage, PropertyStorageContent(owner, field))).ToArray();

    internal static void StampExtensionCore(BirFunctionDeclaration wrapper, string core)
        => wrapper.Attrs = wrapper.Attrs.OrEmpty().Append(PlacementCarrier(AKExtensionCore, ExtensionCoreContent(core))).ToArray();

    internal static void AddSourceMethodIdentity(BirFunctionDeclaration method, string sourceName,
        string sourceDeclaration = null) => method.Attrs = method.Attrs.OrEmpty()
            .Append(PlacementCarrier(AKSourceMethod, SourceMethodIdentityContent(sourceName, sourceDeclaration))).ToArray();
}
