#nullable enable
using System;
using System.Buffers;
using System.Text.Json;
using MessagePack;

namespace DotKt.Bir;

public static partial class BirCarrier
{
    public const string MessagePackV1 = "bir-msgpack/1";
    public delegate T MessagePackBodyReader<T>(ref MessagePackReader reader);
    public delegate void MessagePackBodyWriter<T>(ref MessagePackWriter writer, T value);

    public static byte[] EncodeBody<T>(string codec, T body, Action<Utf8JsonWriter, T> json,
        MessagePackBodyWriter<T> binary)
    {
        if (codec == JsonV1) return EncodeBody(codec, body, json);
        if (codec != MessagePackV1) throw new NotSupportedException($"unknown carrier codec `{codec}`");
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
        if (codec != MessagePackV1) throw new NotSupportedException($"unknown carrier codec `{codec}`");
        var reader = new MessagePackReader(content);
        var body = binary(ref reader);
        if (!reader.End) throw new FormatException();
        return body;
    }
}
