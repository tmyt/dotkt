using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;
using DotKt.Cir;

internal static class BirLiteralTests
{
    public static void Run(XDocument report)
    {
        const string prefix = "IR_BIR_LITERAL_SAMPLE ";
        var count = 0;
        var widths = new HashSet<int>();
        var project = typeof(BirLiteral).Assembly.GetType("AttributeProjection", true)!
            .GetMethod("Literal", BindingFlags.Public | BindingFlags.Static, [typeof(JsonNode), typeof(TypeNode)])!;
        foreach (var line in report.Descendants("system-out").SelectMany(output => output.Value.Split('\n')))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var node = JsonNode.Parse(line[prefix.Length..].TrimEnd('\r'))!;
            var literal = BirLiteral.Read(node["value"]);
            if (!JsonNode.DeepEquals(literal.ToDom(), node["value"]))
                throw new Exception("Actual Kotlin scalar changed across the typed BIR JSON boundary");
            var type = literal switch {
                Float32Literal => new TypeNode.Fqn("System.Single"),
                Float64Literal => new TypeNode.Fqn("System.Double"),
                _ => null,
            };
            if (type is not null)
            {
                var physical = project.Invoke(null, [node["value"], type]);
                if (literal is Float32Literal f32)
                {
                    widths.Add(32);
                    if (physical is not AttributeArgument.Float32Value { Bits: var bits } || bits != f32.Bits)
                        throw new Exception("Kotlin Float bits changed at the owning physical projection");
                }
                if (literal is Float64Literal f64)
                {
                    widths.Add(64);
                    if (physical is not AttributeArgument.Float64Value { Bits: var bits } || bits != f64.Bits)
                        throw new Exception("Kotlin Double bits changed at the owning physical projection");
                }
            }
            count++;
        }
        if (count == 0 || !widths.SetEquals([32, 64])) throw new Exception("Actual Kotlin BIR literal samples missing");
        Console.WriteLine($"Actual Kotlin IR -> typed BIR literals -> physical scalar projection: {count} samples passed");
    }
}
