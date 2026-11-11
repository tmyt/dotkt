using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;

static class TypeParameterTests
{
    internal static void Run(string path)
    {
        var report = XDocument.Load(path);
        var suite = report.Root!;
        if ((int)suite.Attribute("tests")! == 0 || new[] { "failures", "errors", "skipped" }
            .Any(name => (int)suite.Attribute(name)! != 0))
            throw new Exception("Kotlin declaration tests did not complete");
        const string prefix = "IR_TYPE_PARAMETER_SAMPLE ";
        var samples = 0;
        foreach (var line in report.Descendants("system-out").SelectMany(output => output.Value.Split('\n')))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var source = JsonNode.Parse(line[prefix.Length..])!.AsArray();
            foreach (var node in source)
            {
                var declaration = TypeParameterDeclaration.Read(node!);
                if (!JsonNode.DeepEquals(node, declaration.ToDom()))
                    throw new Exception("Kotlin/C# declaration frame mismatch");
                // Independent copies preserve positional bounds rather than remapping or sharing DOM ownership.
                var clone = TypeParameterDeclaration.Read(declaration.ToDom());
                if (clone.Name != declaration.Name || clone.Reified != declaration.Reified
                    || clone.Variance != declaration.Variance
                    || !JsonNode.DeepEquals(clone.ToDom(), declaration.ToDom()))
                    throw new Exception("Generic declaration model lost source facts");
                samples++;
            }
        }
        if (samples != 3) throw new Exception($"Expected all three actual Kotlin declaration samples, got {samples}");
        foreach (var json in new[] { """{"constraints":[]}""", """{"name":"T","constraints":[null]}""",
            """{"name":"T","unexpected":0}""" })
        {
            var rejected = false;
            try { TypeParameterDeclaration.Read(JsonNode.Parse(json)!); }
            catch (Exception) { rejected = true; }
            if (!rejected) throw new Exception("Malformed current generic declaration was accepted");
        }
        Console.WriteLine($"Actual Kotlin IR -> typed generic declarations -> C# declaration frames: {samples} slots passed");
    }
}
