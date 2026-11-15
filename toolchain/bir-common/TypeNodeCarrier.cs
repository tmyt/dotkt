#nullable enable
namespace DotKt.Bir;

// Declaration metadata contains one TypeNode, not an executable graph or a
// general JSON object. The constructor's explicit codec selects its reader.
public static class TypeNodeCarrier
{
    public const string Codec = BirCarrier.MessagePackV1;

    public static byte[] Encode(string codec, TypeNode value) => BirCarrier.EncodeBody(
        codec, value, TypeNode.WriteJson, TypeNodeMessagePack.Write);

    public static TypeNode Decode(string codec, byte[] content) => BirCarrier.DecodeBody(
        codec, content, TypeNode.ReadJson, TypeNodeMessagePack.Read);
}
