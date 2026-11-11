using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;
using DotKt.Cir;
using A = DotKt.Cir.AttributeArgument;

internal static class BirLiteralTests
{
    public static void Run(XDocument report)
    {
        const string prefix = "IR_BIR_LITERAL_SAMPLE ";
        var count = 0;
        var inventory = new Dictionary<string, int>();
        var project = typeof(BirLiteral).Assembly.GetType("AttributeProjection", true)!
            .GetMethod("Literal", BindingFlags.Public | BindingFlags.Static, [typeof(JsonNode), typeof(TypeNode)])!;
        foreach (var line in report.Descendants("system-out").SelectMany(output => output.Value.Split('\n')))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var node = JsonNode.Parse(line[prefix.Length..].TrimEnd('\r'))!;
            var literal = BirLiteral.Read(node["value"]);
            if (!JsonNode.DeepEquals(literal.ToDom(), node["value"]))
                throw new Exception("Actual Kotlin scalar changed across the typed BIR JSON boundary");
            var sourceType = TypeNode.Read(node["type"])!;
            var key = sourceType switch {
                TypeNode.Fqn fqn => fqn.Name,
                TypeNode.Nullable { Of: TypeNode.Fqn fqn } => fqn.Name + "?",
                _ => throw new Exception("Unexpected IR scalar sample type"),
            };
            // Test inputs name their Kotlin source type. The fixture supplies the
            // expected physical type/value; the scalar tag does not choose it.
            (string Name, A Expected) expectation = key switch {
                "kotlin.Float" => ("System.Single", new A.Float32Value(((Float32Literal)literal).Bits)),
                "kotlin.Double" => ("System.Double", new A.Float64Value(((Float64Literal)literal).Bits)),
                "kotlin.Any?" => ("System.Object", new A.NullValue()),
                "kotlin.Boolean" => ("System.Boolean", new A.BooleanValue(((BirLiteral.BooleanLiteral)literal).Value)),
                "kotlin.Char" => ("System.Char", new A.CharValue(0xd800)),
                "kotlin.Byte" => ("System.SByte", new A.Int8Value(sbyte.MinValue)),
                "kotlin.Short" => ("System.Int16", new A.Int16Value(short.MinValue)),
                "kotlin.Int" => ("System.Int32", new A.Int32Value(int.MinValue)),
                "kotlin.Long" => ("System.Int64", new A.Int64Value(long.MinValue)),
                "kotlin.String" => ("System.String", new A.Utf16Value([0x61, 0xd800, 0, 0xdfff])),
                _ => throw new Exception("Unaccounted IR scalar sample type"),
            };
            var (name, expected) = expectation;
            var physical = project.Invoke(null, [node["value"], new TypeNode.Fqn(name)]);
            var equal = expected is A.Utf16Value units
                ? physical is A.Utf16Value actual && actual.Units.SequenceEqual(units.Units)
                : expected.Equals(physical);
            if (!equal) throw new Exception($"Kotlin {key} changed at the owning physical scalar projection");
            inventory[key] = inventory.GetValueOrDefault(key) + 1;
            count++;
        }
        var expectedInventory = new Dictionary<string, int> {
            ["kotlin.Float"] = 7, ["kotlin.Double"] = 7, ["kotlin.Boolean"] = 2,
            ["kotlin.Any?"] = 1, ["kotlin.Char"] = 1, ["kotlin.Byte"] = 1,
            ["kotlin.Short"] = 1, ["kotlin.Int"] = 1, ["kotlin.Long"] = 1, ["kotlin.String"] = 1,
        };
        if (count != 23 || inventory.Count != expectedInventory.Count
            || expectedInventory.Any(pair => inventory.GetValueOrDefault(pair.Key) != pair.Value))
            throw new Exception("Actual Kotlin BIR literal sample inventory changed");
        Console.WriteLine($"Actual Kotlin IR -> typed BIR literals -> physical scalar projection: {count} samples passed");
    }
}
