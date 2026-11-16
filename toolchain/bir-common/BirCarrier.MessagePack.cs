#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;
using MessagePack;

namespace DotKt.Bir;

public static partial class BirCarrier
{
    public const string MessagePackV1 = "bir-msgpack/1";
    public const string MessagePackDeflateV1 = "bir-msgpack+deflate/1";
    public delegate T MessagePackBodyReader<T>(ref MessagePackReader reader);
    public delegate void MessagePackBodyWriter<T>(ref MessagePackWriter writer, T value);

    public static byte[] EncodeBody<T>(string codec, T body, Action<Utf8JsonWriter, T> json,
        MessagePackBodyWriter<T> binary)
    {
        if (codec == JsonV1) return EncodeBody(codec, body, json);
        if (codec is not (MessagePackV1 or MessagePackDeflateV1))
            throw new NotSupportedException($"unknown carrier codec `{codec}`");
        var output = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(output);
        binary(ref writer, body);
        writer.Flush();
        var content = output.WrittenSpan.ToArray();
        return codec == MessagePackDeflateV1 ? Deflate(content) : content;
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
        // The explicit raw inflater exposes completion and remaining input.
        // DeflateStream's default reader can accept an unfinished stream and
        // read ahead past its end; neither is valid for a self-contained carrier.
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(content);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!inflater.IsFinished)
            {
                var count = inflater.Inflate(buffer);
                if (count != 0) output.Write(buffer, 0, count);
                else if (!inflater.IsFinished) throw new FormatException("Incomplete DEFLATE carrier");
            }
            if (inflater.RemainingInput != 0) throw new FormatException("Trailing DEFLATE carrier data");
            return output.ToArray();
        }
        catch (SharpZipBaseException error)
        {
            throw new FormatException("Malformed DEFLATE carrier", error);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
