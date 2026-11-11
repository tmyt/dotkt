#nullable enable
using System;
using System.Buffers;
using System.IO;
using MessagePack;

namespace DotKt.Cir;

public enum CirFileFormat { Json, MessagePack }

public static class CirFileCodec
{
    // Explicit artifact kind and codec, not a compatibility or migration version.
    private static ReadOnlySpan<byte> Header => "DOTKT\0CIR\0MSGPACK\0"u8;

    public static byte[] Encode(CirFileDeclaration file)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Header.CopyTo(buffer.GetSpan(Header.Length));
        buffer.Advance(Header.Length);
        var writer = new MessagePackWriter(buffer);
        CirMessagePack.Write(ref writer, file);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static CirFileDeclaration Decode(ReadOnlyMemory<byte> bytes)
    {
        if (!bytes.Span.StartsWith(Header)) throw new FormatException();
        var reader = new MessagePackReader(bytes[Header.Length..]);
        var file = CirMessagePack.Read(ref reader);
        if (!reader.End) throw new FormatException();
        return file;
    }

    public static CirFileDeclaration Read(string path)
    {
        if (path.EndsWith(".cir.msgpack", StringComparison.Ordinal)) return Decode(File.ReadAllBytes(path));
        if (path.EndsWith(".json", StringComparison.Ordinal)) return DeclarationJson.ReadFile(path);
        throw new FormatException();
    }

    public static void Write(string path, CirFileDeclaration file, CirFileFormat format)
    {
        switch (format)
        {
            case CirFileFormat.Json: File.WriteAllText(path, DeclarationJson.Write(file, indented: true)); break;
            case CirFileFormat.MessagePack: File.WriteAllBytes(path, Encode(file)); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
}
