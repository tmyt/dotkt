using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;

internal static class SourceMetadataTests
{
    public static void Run(string reportPath)
    {
        MessagePackBoundaryTests.Run();
        var report = XDocument.Load(reportPath);
        const string prefix = "IR_SOURCE_METADATA_SAMPLE ";
        var samples = report.Descendants("system-out").SelectMany(output => output.Value.Split('\n'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].TrimEnd('\r')).ToArray();
        if (samples.Length == 0) throw new Exception("No actual Kotlin source metadata samples");
        foreach (var json in samples)
        {
            var expected = JsonNode.Parse(json)!;
            var source = Read(json);
            Equal(expected, Write(source));
            SourceMetadataProjectionTests.Run(json);
            // Property order is not part of JSON, including nested union discriminators.
            Equal(expected, Write(Read(Reverse(expected).ToJsonString())));
            Equal(expected, Write(MessagePackBoundaryTests.Read(MessagePackBoundaryTests.Encode(Reverse(expected)))));

            // Exercise the actual production input boundary and freezer, not a second codec copy.
            var tool = typeof(SourceTypeAlias).Assembly;
            var input = new JsonObject { ["k"] = "file", ["typeAliases"] = new JsonArray(expected.DeepClone()) };
            var readInput = tool.GetType("BirInputJson", true)!.GetMethod("Read")!;
            var (root, aliases) = ((JsonObject, SourceTypeAlias[]))readInput.Invoke(null,
                [Encoding.UTF8.GetBytes(input.ToJsonString())])!;
            if (root.ContainsKey("typeAliases") || aliases.Length != 1)
                throw new Exception("Source declarations leaked into physical DOM traversal");
            tool.GetType("RoundtripMetadata", true)!.GetMethod("FreezeTypeAliases")!.Invoke(null, [root, aliases]);
            var arguments = root["assemblyAttrs"]![0]!["args"]!;
            if (arguments[0]!["value"]!.GetValue<string>() != BirCarrier.MessagePackV1)
                throw new Exception("Source alias carrier is not actually binary");
            var decoded = BirCarrier.DecodeBody(arguments[0]!["value"]!.GetValue<string>(),
                Convert.FromBase64String(arguments[1]!["bytes"]!.GetValue<string>()),
                SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
            Equal(expected, Write(decoded));
            var binary = BirCarrier.EncodeBody(BirCarrier.MessagePackV1, source,
                SourceMetadataJson.Write, SourceMetadataMessagePack.Write);
            Equal(expected, Write(BirCarrier.DecodeBody(BirCarrier.MessagePackV1, binary,
                SourceMetadataJson.Read, SourceMetadataMessagePack.Read)));
            try
            {
                BirCarrier.DecodeBody(BirCarrier.MessagePackV1, [.. binary, 0xc0],
                    SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
                throw new Exception("Trailing binary payload was accepted");
            }
            catch (FormatException) { }
            try
            {
                BirCarrier.DecodeBody("unknown/1", binary, SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
                throw new Exception("Unknown codec was accepted");
            }
            catch (NotSupportedException) { }

            void Malformed(Action<JsonNode> mutate)
            {
                var node = expected.DeepClone();
                mutate(node);
                var malformedJson = node.ToJsonString();
                var malformedBinary = MessagePackBoundaryTests.Encode(node);
                MessagePackBoundaryTests.Reject(() => Read(malformedJson));
                MessagePackBoundaryTests.Reject(() => MessagePackBoundaryTests.Read(malformedBinary));
            }
            Malformed(node => node.AsObject().Remove("name"));
            Malformed(node => node["name"] = null);
            Malformed(node => node["annotations"] = new JsonArray((JsonNode?)null));
            Malformed(node => node["underlyingType"]!["kind"] = "unknown");
            Malformed(node => node["expandedType"]!["arguments"]![3]!["type"] = node["underlyingType"]!.DeepClone());
            Malformed(node => node["underlyingType"]!["id"] = 0);
            var duplicateBinary = MessagePackBoundaryTests.Encode(expected, duplicateName: true);
            MessagePackBoundaryTests.Reject(() => MessagePackBoundaryTests.Read(duplicateBinary));
            MessagePackBoundaryTests.ExcessiveDepth(expected);
            try { Read(json[..^1] + ",\"name\":\"duplicate\"}"); }
            catch (FormatException) { continue; }
            throw new Exception("Duplicate source metadata property was accepted");
        }
        Console.WriteLine($"Kotlin -> C# source metadata, input isolation and carrier codec: {samples.Length} samples passed");
        // A valid current document may exceed the writer's default 1000 levels
        // while staying inside the shared 1024-level IR boundary.
        SourceType deep = new SourceType.ParameterType(0, false, false, false, [], []);
        var classifier = new SourceClassifier("sample", [], "Deep");
        for (var i = 0; i < 1001; i++)
            deep = new SourceType.ClassType(classifier, false, false, false, [], [], OuterType: deep);
        var deepAlias = new SourceTypeAlias("sample", [], "DeepAlias", "public", [], deep, deep, []);
        var deepBytes = BirCarrier.EncodeBody(BirCarrier.JsonV1, deepAlias, SourceMetadataJson.Write);
        var restored = BirCarrier.DecodeBody(BirCarrier.JsonV1, deepBytes, SourceMetadataJson.Read).ExpandedType;
        CheckDepth(restored);
        var deepBinary = BirCarrier.EncodeBody(BirCarrier.MessagePackV1, deepAlias,
            SourceMetadataJson.Write, SourceMetadataMessagePack.Write);
        CheckDepth(BirCarrier.DecodeBody(BirCarrier.MessagePackV1, deepBinary,
            SourceMetadataJson.Read, SourceMetadataMessagePack.Read).ExpandedType);

        const string binaryPrefix = "IR_SOURCE_METADATA_MSGPACK ";
        const string utf16Prefix = "IR_SOURCE_METADATA_UTF16_MSGPACK ";
        var binaryCount = 0;
        foreach (var line in report.Descendants("system-out").SelectMany(output => output.Value.Split('\n')))
        {
            var utf16 = line.StartsWith(utf16Prefix, StringComparison.Ordinal);
            if (!utf16 && !line.StartsWith(binaryPrefix, StringComparison.Ordinal)) continue;
            var encoded = Convert.FromBase64String(line[(utf16 ? utf16Prefix.Length : binaryPrefix.Length)..].TrimEnd('\r'));
            var source = BirCarrier.DecodeBody(BirCarrier.MessagePackV1, encoded,
                SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
            var reencoded = BirCarrier.EncodeBody(BirCarrier.MessagePackV1, source,
                SourceMetadataJson.Write, SourceMetadataMessagePack.Write);
            source = BirCarrier.DecodeBody(BirCarrier.MessagePackV1, reencoded,
                SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
            if (utf16)
            {
                if (((SourceAnnotationValue.StringValue)source.Annotations.Single().Arguments.Single().Value).Value
                    != "prefix\ud800middle\udc00suffix😀\0") throw new Exception("Kotlin/C# UTF-16 units changed");
            }
            else Equal(JsonNode.Parse(samples.Single())!, Write(source));
            binaryCount++;
        }
        if (binaryCount != 2) throw new Exception("Missing cross-language MessagePack samples");
        Console.WriteLine("Kotlin -> C# MessagePack metadata, raw bits and lone UTF-16 surrogate units: passed");
    }

    static SourceTypeAlias Read(string json) => BirCarrier.DecodeBody(BirCarrier.JsonV1,
        Encoding.UTF8.GetBytes(json), SourceMetadataJson.Read);
    static void CheckDepth(SourceType restored)
    {
        var levels = 0;
        while (restored.OuterType is { } outer) { levels++; restored = outer; }
        if (levels != 1001) throw new Exception("Deep source metadata was truncated");
    }
    static JsonNode Write(SourceTypeAlias source) => JsonNode.Parse(
        BirCarrier.EncodeBody(BirCarrier.JsonV1, source, SourceMetadataJson.Write))!;
    static void Equal(JsonNode expected, JsonNode actual)
    {
        if (!JsonNode.DeepEquals(expected, actual)) throw new Exception("Source metadata facts changed in transit");
    }
    static JsonNode Reverse(JsonNode node) => node switch {
        JsonObject obj => new JsonObject(obj.Reverse().Select(pair =>
            KeyValuePair.Create(pair.Key, pair.Value is null ? null : Reverse(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Reverse(item)).ToArray()),
        _ => node.DeepClone(),
    };
}
