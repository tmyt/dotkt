using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using MessagePack;

internal static class MessagePackBoundaryTests
{
    internal static void Run()
    {
        // A str payload must contain actual UTF-8, not decode to a different name/value.
        Reject(() => Text([0xa2, 0xc3, 0x28]));
        Reject(() => Text([0xd4, 2, 0])); // unknown application extension
        Reject(() => Text([0xd4, 1, 0])); // odd UTF-16 byte count
        Reject(() => Text([0xc0]));
        if (Text(Sequence([0xa6, 0xce, 0xba, 0xf0], [0x9f, 0x98, 0x80])) != "κ😀")
            throw new Exception("Segmented UTF-8 string changed");
        Reject(() => Text(Sequence([0xa2, 0xc3], [0x28])));
    }

    internal static byte[] Encode(JsonNode value, bool duplicateName = false)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(output);
        if (duplicateName)
        {
            var root = value.AsObject();
            writer.WriteMapHeader(root.Count + 1);
            foreach (var (key, child) in root) { writer.Write(key); Write(ref writer, child); }
            writer.Write("name"); writer.Write("duplicate");
        }
        else Write(ref writer, value);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    internal static void Reject(Action decode)
    {
        try { decode(); }
        catch (Exception error) when (error is FormatException or MessagePackSerializationException
            or EndOfStreamException or DecoderFallbackException) { return; }
        throw new Exception("Malformed current codec data was accepted");
    }

    internal static SourceTypeAlias Read(byte[] data) => BirCarrier.DecodeBody(BirCarrier.MessagePackV1, data,
        SourceMetadataJson.Read, SourceMetadataMessagePack.Read);

    internal static void ExcessiveDepth(JsonNode original)
    {
        JsonNode nested = JsonNode.Parse("""{"kind":"parameter","id":0,"nullable":false,"suspend":false,"definitelyNotNull":false,"arguments":[],"annotations":[]}""")!;
        for (var i = 0; i < BirJson.MaxDepth; i++) nested = new JsonObject {
            ["kind"] = "class", ["classifier"] = new JsonObject { ["packageName"] = "sample", ["ownerPath"] = new JsonArray(), ["name"] = "Deep" },
            ["nullable"] = false, ["suspend"] = false, ["definitelyNotNull"] = false,
            ["arguments"] = new JsonArray(), ["annotations"] = new JsonArray(), ["outerType"] = nested,
        };
        var invalid = original.DeepClone();
        invalid["expandedType"] = nested;
        var encoded = Encode(invalid);
        Reject(() => Read(encoded));
    }

    static string Text(byte[] data)
    {
        var reader = new MessagePackReader(data);
        return IrMessagePack.ReadString(ref reader);
    }

    static string Text(ReadOnlySequence<byte> data)
    {
        var reader = new MessagePackReader(data);
        return IrMessagePack.ReadString(ref reader);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(byte[] bytes) { Memory = bytes; }
        internal Segment Append(byte[] bytes)
        {
            var tail = new Segment(bytes);
            tail.RunningIndex = RunningIndex + Memory.Length;
            Next = tail;
            return tail;
        }
    }

    static ReadOnlySequence<byte> Sequence(byte[] first, byte[] second)
    {
        var head = new Segment(first);
        var tail = head.Append(second);
        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }

    // Test-only alternate writer for mutating the same current-model cases used
    // by the JSON tests. Production encoding never visits a JSON DOM.
    static void Write(ref MessagePackWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null: writer.WriteNil(); break;
            case JsonObject obj:
                writer.WriteMapHeader(obj.Count);
                foreach (var (key, value) in obj) { writer.Write(key); Write(ref writer, value); }
                break;
            case JsonArray array:
                writer.WriteArrayHeader(array.Count);
                foreach (var value in array) Write(ref writer, value);
                break;
            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String: writer.Write(node.GetValue<string>()); break;
                    case JsonValueKind.Number:
                        if (node.AsValue().TryGetValue<long>(out var wide)) writer.Write(wide);
                        else writer.Write(node.GetValue<int>());
                        break;
                    case JsonValueKind.True: writer.Write(true); break;
                    case JsonValueKind.False: writer.Write(false); break;
                    default: throw new Exception("Unexpected source metadata test value");
                }
                break;
        }
    }
}
