using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;

internal static class SourceMetadataTests
{
    public static void Run(string reportPath)
    {
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
            var decoded = BirCarrier.DecodeBody(arguments[0]!["value"]!.GetValue<string>(),
                Convert.FromBase64String(arguments[1]!["bytes"]!.GetValue<string>()), SourceMetadataJson.Read);
            Equal(expected, Write(decoded));

            void Malformed(Action<JsonNode> mutate)
            {
                var node = expected.DeepClone();
                mutate(node);
                try { Read(node.ToJsonString()); }
                catch (FormatException) { return; }
                throw new Exception("Malformed current source metadata was accepted");
            }
            Malformed(node => node.AsObject().Remove("name"));
            Malformed(node => node["name"] = null);
            Malformed(node => node["annotations"] = new JsonArray((JsonNode?)null));
            Malformed(node => node["underlyingType"]!["kind"] = "unknown");
            Malformed(node => node["expandedType"]!["arguments"]![3]!["type"] = node["underlyingType"]!.DeepClone());
            Malformed(node => node["underlyingType"]!["id"] = 0);
            try { Read(json[..^1] + ",\"name\":\"duplicate\"}"); }
            catch (FormatException) { continue; }
            throw new Exception("Duplicate source metadata property was accepted");
        }
        Console.WriteLine($"Kotlin -> C# source metadata, input isolation and carrier codec: {samples.Length} samples passed");
    }

    static SourceTypeAlias Read(string json) => BirCarrier.DecodeBody(BirCarrier.JsonV1,
        Encoding.UTF8.GetBytes(json), SourceMetadataJson.Read);
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
