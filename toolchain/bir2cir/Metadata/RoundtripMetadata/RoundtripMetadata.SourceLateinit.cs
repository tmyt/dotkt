using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    static byte[] LateinitStateContent(string owner, string method, bool isStatic) => BirCarrier.EncodeBody(
        BirCarrier.JsonV1, (Owner: owner, Method: method, Static: isStatic), (writer, value) => {
            writer.WriteStartObject();
            writer.WriteString("owner", value.Owner);
            writer.WriteString("method", value.Method);
            writer.WriteBoolean("static", value.Static);
            writer.WriteEndObject();
        });

    internal static void StampLateinitState(BirFunctionDeclaration getter, string owner, string method, bool isStatic)
        => getter.Attrs = getter.Attrs.OrEmpty().Append(new BirAttribute {
            Attr = new TypeNode.Fqn(AKLateinitState),
            ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
            Args = new IBirAttributeValue[] {
                new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(BirCarrier.JsonV1) },
                new BirOpaqueBytes { Bytes = LateinitStateContent(owner, method, isStatic) },
            },
        }).ToArray();
}
