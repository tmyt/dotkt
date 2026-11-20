using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    static byte[] LateinitStateContent(string owner, string method, bool isStatic) => BirCarrier.EncodeMetadata(
        (owner, method, isStatic), static (ref MessagePack.MessagePackWriter writer, (string owner, string method, bool isStatic) value) => {
            writer.WriteMapHeader(3); writer.Write("owner"); IrMessagePack.WriteString(ref writer, value.owner);
            writer.Write("method"); IrMessagePack.WriteString(ref writer, value.method);
            writer.Write("static"); writer.Write(value.isStatic);
        });

    internal static void StampLateinitState(BirFunctionDeclaration getter, string owner, string method, bool isStatic)
        => getter.Attrs = getter.Attrs.OrEmpty().Append(PlacementCarrier(AKLateinitState, LateinitStateContent(owner, method, isStatic))).ToArray();
}
