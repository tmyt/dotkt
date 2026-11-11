using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;

var report = XDocument.Load(args[0]);
const string prefix = "IR_TYPE_SAMPLE ";
var count = 0;
foreach (var line in report.Descendants("system-out").SelectMany(output => output.Value.Split('\n')))
{
    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
    var json = line[prefix.Length..].TrimEnd('\r');
    var type = TypeNode.Parse(json);
    var encoded = TypeNode.ToJson(type);
    // JSON is used only to compare test outputs, not as the production IR representation.
    if (!JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(encoded)))
        throw new Exception($"Kotlin/C# type codec mismatch: {json} -> {encoded}");
    count++;
}
if (count == 0) throw new Exception("No actual Kotlin type samples were exercised");
Console.WriteLine($"Kotlin -> C# streaming type codec: {count} actual samples passed");
SourceMetadataTests.Run(args[1]);
