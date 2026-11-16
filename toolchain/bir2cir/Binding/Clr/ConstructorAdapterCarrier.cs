using DotKt.Bir;
using DotKt.Bir.Lowering;
using MessagePack;

// Constructor adapters use the same declared codec dispatch and owning phase
// model as file and inline/default executable carriers. Bytes stay opaque to
// ordinary lowering; no body text or legacy carrier probing is involved.
static class ConstructorAdapterCarrier
{
    internal const string AttributeName = "DotKt.Runtime.CompilerServices.KotlinConstructorAdapterAttribute";
    internal const string Codec = BirCarrier.MessagePackV1;

    internal static byte[] Encode(string codec, BirConstructorAdapterPayload payload) => BirCarrier.EncodeBody(codec, payload,
        (writer, value) => value.WriteJson(writer),
        (ref MessagePackWriter writer, BirConstructorAdapterPayload value) =>
            BirGraphMessagePack.WriteBirConstructorAdapterPayload(ref writer, value, 0));

    internal static BirConstructorAdapterPayload Decode(string codec, byte[] content)
    {
        var payload = BirCarrier.DecodeBody(codec, content, BirGraphJson.ReadBirConstructorAdapterPayload,
            BirGraphMessagePack.ReadBirConstructorAdapterPayload);
        if (payload.Parameters.Value.Length != payload.Signature.Value.Length
            || payload.Arguments.Value.Length != payload.TerminalSignature.Value.Length)
            throw new FormatException("Constructor-adapter signature and argument arities differ");
        _ = payload.Statements.Value;
        return payload;
    }

    internal static bool Same(BirConstructorAdapterPayload left, BirConstructorAdapterPayload right) => ReferenceEquals(left, right)
        || Encode(Codec, left).AsSpan().SequenceEqual(Encode(Codec, right));

    internal static BirAttribute Create(BirConstructorAdapterPayload source)
    {
        var outgoing = (BirConstructorAdapterPayload)source.DeepClone();
        HideSourceSpans(outgoing);
        return new BirAttribute {
            Attr = new TypeNode.Fqn(AttributeName),
            ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
            Args = new IBirAttributeValue[] {
                new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(Codec) },
                new BirOpaqueBytes { Bytes = Encode(Codec, outgoing) },
            },
        };
    }

    static void HideSourceSpans(BirGraphValue root) => root.Visit(node => {
        if (node is BirSourcePoint point) point.Span = default;
        if (node is not BirAttribute attribute
            || attribute.Attr.OrDefault() is not TypeNode.Fqn { Name: DefaultBirCarrier.AttributeName }) return;
        var arguments = attribute.Args.Value;
        var codec = ((BirConstant)arguments[1]).Value.Value is BirLiteral.TextLiteral literal
            ? literal.Value : throw new FormatException("Default carrier has no codec");
        var bytes = (BirOpaqueBytes)arguments[2];
        var body = (BirGraphValue)ExecutableBirCodec.ReadExpression(codec, bytes.Bytes.Value);
        HideSourceSpans(body);
        bytes.Bytes = ExecutableBirCodec.EncodeExpression(codec, (IBirExpression)body);
    });
}
