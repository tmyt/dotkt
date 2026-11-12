using System.Buffers;
using DotKt.Bir.Lowering;
using DotKt.Bir;
using MessagePack;

enum BirFileFormat { Json, MessagePack }

// Explicit artifact and codec framing. Input construction always owns typed BIR;
// neither transport reconstructs semantics or reparses a serialized IR graph.
static partial class BirFileCodec
{
    static ReadOnlySpan<byte> Header => "DOTKT\0BIR\0MSGPACK\0"u8;

    internal static BirFileDeclaration Read(string path)
    {
        var content = File.ReadAllBytes(path);
        if (path.EndsWith(".bir.msgpack", StringComparison.Ordinal)) return Decode(content);
        if (path.EndsWith(".json", StringComparison.Ordinal)) return BirGraphJson.ReadFile(content);
        throw new FormatException();
    }

    internal static byte[] Encode(BirFileDeclaration file)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Header.CopyTo(buffer.GetSpan(Header.Length));
        buffer.Advance(Header.Length);
        var writer = new MessagePackWriter(buffer);
        BirGraphMessagePack.Write(ref writer, file);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static BirFileDeclaration Decode(ReadOnlyMemory<byte> content)
    {
        if (!content.Span.StartsWith(Header)) throw new FormatException();
        var reader = new MessagePackReader(content[Header.Length..]);
        var file = BirGraphMessagePack.Read(ref reader);
        if (!reader.End) throw new FormatException();
        return file;
    }

    internal static void Write(string path, BirFileDeclaration file, BirFileFormat format)
    {
        switch (format)
        {
            case BirFileFormat.Json:
                File.WriteAllBytes(path, BirCarrier.EncodeBody(BirCarrier.JsonV1, file, (writer, value) => value.WriteJson(writer)));
                break;
            case BirFileFormat.MessagePack: File.WriteAllBytes(path, Encode(file)); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
