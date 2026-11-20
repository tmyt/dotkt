#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;

namespace DotKt.Bir;

public static partial class BirCarrier
{
    public const string MessagePackV1 = "bir-msgpack/1";
    public const string MessagePackDeflateV1 = "bir-msgpack+deflate/1";
    public delegate T MessagePackBodyReader<T>(ref MessagePackReader reader);
    public delegate void MessagePackBodyWriter<T>(ref MessagePackWriter writer, T value);

    // Embedded declaration records use the same uncompressed logical transport as
    // executable BIR; only the final attribute projection chooses compression.
    public static byte[] EncodeMetadata<T>(T value, MessagePackBodyWriter<T> write) => Pack(value, write);

    // Importers that consume a declaration as a property map need its logical
    // fields, not an executable BIR decoder or a JSON text round trip.
    public static JsonNode DecodeMetadata(string codec, byte[] content)
    {
        if (codec == MessagePackDeflateV1) content = Inflate(content);
        else if (codec != MessagePackV1) throw new NotSupportedException($"unknown metadata codec `{codec}`");
        var reader = new MessagePackReader(content);
        var value = ReadMetadataValue(ref reader, 0);
        if (!reader.End || value is not JsonObject) throw new FormatException("Expected one metadata record");
        return value;
    }

    static JsonNode? ReadMetadataValue(ref MessagePackReader reader, int depth)
    {
        if (depth > BirJson.MaxDepth) throw new FormatException("Metadata nesting exceeds the BIR limit");
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                var map = new JsonObject();
                var fields = reader.ReadMapHeader();
                for (var i = 0; i < fields; i++)
                    map.Add(IrMessagePack.ReadString(ref reader), ReadMetadataValue(ref reader, depth + 1));
                return map;
            case MessagePackType.Array:
                var array = new JsonArray();
                var count = reader.ReadArrayHeader();
                for (var i = 0; i < count; i++) array.Add(ReadMetadataValue(ref reader, depth + 1));
                return array;
            case MessagePackType.String:
            case MessagePackType.Extension: return JsonValue.Create(IrMessagePack.ReadString(ref reader));
            case MessagePackType.Integer: return JsonValue.Create(reader.ReadInt32());
            case MessagePackType.Boolean: return JsonValue.Create(reader.ReadBoolean());
            case MessagePackType.Nil: reader.ReadNil(); return null;
            default: throw new FormatException("Unsupported declaration metadata field");
        }
    }

    public static byte[] EncodeBody<T>(string codec, T body, Action<Utf8JsonWriter, T> json,
        MessagePackBodyWriter<T> binary)
    {
        if (codec == JsonV1) return EncodeBody(codec, body, json);
        if (codec is not (MessagePackV1 or MessagePackDeflateV1))
            throw new NotSupportedException($"unknown carrier codec `{codec}`");
        var content = Pack(body, binary);
        return codec == MessagePackDeflateV1 ? Deflate(content) : content;
    }

    static byte[] Pack<T>(T body, MessagePackBodyWriter<T> binary)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(output);
        binary(ref writer, body);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    public static T DecodeBody<T>(string codec, byte[] content, JsonBodyReader<T> json,
        MessagePackBodyReader<T> binary)
    {
        if (codec == JsonV1) return DecodeBody(codec, content, json);
        if (codec == MessagePackDeflateV1) content = Inflate(content);
        else if (codec != MessagePackV1) throw new NotSupportedException($"unknown carrier codec `{codec}`");
        var reader = new MessagePackReader(content);
        var body = binary(ref reader);
        if (!reader.End) throw new FormatException();
        return body;
    }

    // Select the transport once, at the outgoing attribute boundary. Debug and
    // small bodies never pay for a compression attempt. The measured stdlib
    // corpus retained all useful savings at 128 bytes; require 32 bytes and 10%
    // net savings after accounting for the longer declared codec ID.
    public static (string Codec, byte[] Content) SelectTransport(string codec, byte[] content, bool release)
    {
        if (!release || codec != MessagePackV1 || content.Length < 128) return (codec, content);
        var compressed = Deflate(content);
        var saved = (long)content.Length - compressed.Length - (MessagePackDeflateV1.Length - MessagePackV1.Length);
        return saved >= 32 && saved * 10 >= content.Length
            ? (MessagePackDeflateV1, compressed) : (codec, content);
    }

    static byte[] Deflate(byte[] content)
    {
        using var output = new MemoryStream();
        // DeflateStream writes RFC 1951, without a zlib or gzip wrapper. Quality
        // is an encoder policy, independent of the logical BIR schema/codec ID.
        using (var stream = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            stream.Write(content);
        return output.ToArray();
    }

    static byte[] Inflate(byte[] content)
    {
        using var input = new DeflateCarrierInput(content);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int count;
            // Do not use DeflateStream.CopyTo: its optimized implementation
            // pushes the entire source through the inflater, including unused
            // input. Read returns zero only after the stream's actual end.
            while ((count = inflater.Read(buffer, 0, buffer.Length)) != 0)
                output.Write(buffer, 0, count);
            if (input.Position != content.Length) throw new FormatException("Trailing DEFLATE carrier data");
            return output.ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new FormatException("Malformed DEFLATE carrier", error);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Reserve the final byte for a separate read. The inflater cannot consume
    // it by reading ahead past an earlier end-of-stream; any trailing bytes
    // therefore leave Position short of Length. A request beyond the input
    // throws instead of returning zero, so truncated streams cannot appear
    // complete even on runtimes with permissive DeflateStream EOF handling.
    // All preceding input is still supplied in bulk.
    sealed class DeflateCarrierInput(byte[] content) : Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            var remaining = content.Length - position;
            if (remaining == 0) throw new FormatException("Incomplete DEFLATE carrier");
            var count = Math.Min(buffer.Length, remaining == 1 ? 1 : remaining - 1);
            content.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
