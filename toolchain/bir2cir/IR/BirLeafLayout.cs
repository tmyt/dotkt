global using FileBirMessagePack = DotKt.Bir.Lowering.BirGraphMessagePack<DotKt.Bir.Lowering.FileBirLeafLayout>;
global using EmbeddedBirMessagePack = DotKt.Bir.Lowering.BirGraphMessagePack<DotKt.Bir.Lowering.EmbeddedBirLeafLayout>;
using System.Text.Json;
using MessagePack;

namespace DotKt.Bir.Lowering;

// The annotated leaves are strings in the inter-stage file schema. Attributes
// carry their structures; ordinary BIR string fields never pass through here.
internal interface IBirLeafLayout
{
    static abstract TypeNode ReadType(ref MessagePackReader reader);
    static abstract void WriteType(ref MessagePackWriter writer, TypeNode type, int depth);
    static abstract string ReadRecord(ref MessagePackReader reader);
    static abstract void WriteRecord(ref MessagePackWriter writer, string frozen, int depth);
}

internal readonly struct FileBirLeafLayout : IBirLeafLayout
{
    public static TypeNode ReadType(ref MessagePackReader reader) => TypeNode.Parse(IrMessagePack.ReadString(ref reader));
    public static void WriteType(ref MessagePackWriter writer, TypeNode type, int depth) => IrMessagePack.WriteString(ref writer, TypeNode.ToJson(type));
    public static string ReadRecord(ref MessagePackReader reader) => IrMessagePack.ReadString(ref reader);
    public static void WriteRecord(ref MessagePackWriter writer, string frozen, int depth) => IrMessagePack.WriteString(ref writer, frozen);
}

internal readonly struct EmbeddedBirLeafLayout : IBirLeafLayout
{
    public static TypeNode ReadType(ref MessagePackReader reader) => TypeNodeMessagePack.Read(ref reader);
    public static void WriteType(ref MessagePackWriter writer, TypeNode type, int depth) => TypeNodeMessagePack.Write(ref writer, type, depth);

    // These leaves retain frozen text in the lowering model. Transcode just
    // that declared leaf, not its surrounding executable graph or literals.
    public static string ReadRecord(ref MessagePackReader reader) =>
        BirCarrier.ReadMetadataValue(ref reader, reader.Depth)?.ToJsonString(BirJson.Writer) ?? throw new FormatException();

    public static void WriteRecord(ref MessagePackWriter writer, string frozen, int depth)
    {
        using var record = JsonDocument.Parse(frozen, BirJson.DocOptions);
        Write(ref writer, record.RootElement, depth);
    }

    static void Write(ref MessagePackWriter writer, JsonElement value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                writer.WriteArrayHeader(value.GetArrayLength());
                foreach (var item in value.EnumerateArray()) Write(ref writer, item, depth + 1);
                break;
            case JsonValueKind.Object:
                writer.WriteMapHeader(value.GetPropertyCount());
                foreach (var field in value.EnumerateObject()) {
                    IrMessagePack.WriteString(ref writer, field.Name);
                    Write(ref writer, field.Value, depth + 1);
                }
                break;
            case JsonValueKind.String: IrMessagePack.WriteString(ref writer, value.GetString()!); break;
            case JsonValueKind.Number: writer.Write(value.GetInt32()); break;
            case JsonValueKind.True: case JsonValueKind.False: writer.Write(value.GetBoolean()); break;
            case JsonValueKind.Null: writer.WriteNil(); break;
            default: throw new FormatException();
        }
    }
}
