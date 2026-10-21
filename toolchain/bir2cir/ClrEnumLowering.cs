using System.Globalization;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

sealed record BasicEnumEntry(string Name, int Ordinal, string PhysicalValue);
sealed record BasicEnumMetadata(string Underlying, IReadOnlyList<BasicEnumEntry> Entries);

// BASIC ENUM DECLARATION LOWERING. kotc has already selected and source-validated the exact Kotlin contract:
// BIR carries ordered Kotlin enum declarations; @ClrEnum additionally supplies an underlying type and constants. This is the
// Kotlin-to-CLR boundary: validate that producer fact defensively, select the legal CLR underlying type, author the
// physical literal values, and retain the ordered map for operations and trusted round-trip metadata.
static class ClrEnumLowering
{
    static readonly Dictionary<string, (string Clr, BigInteger Min, BigInteger Max)> Underlying = new()
    {
        ["kotlin.Byte"] = ("System.SByte", sbyte.MinValue, sbyte.MaxValue),
        ["kotlin.UByte"] = ("System.Byte", byte.MinValue, byte.MaxValue),
        ["kotlin.Short"] = ("System.Int16", short.MinValue, short.MaxValue),
        ["kotlin.UShort"] = ("System.UInt16", ushort.MinValue, ushort.MaxValue),
        ["kotlin.Int"] = ("System.Int32", int.MinValue, int.MaxValue),
        ["kotlin.UInt"] = ("System.UInt32", uint.MinValue, uint.MaxValue),
        ["kotlin.Long"] = ("System.Int64", long.MinValue, long.MaxValue),
        ["kotlin.ULong"] = ("System.UInt64", ulong.MinValue, ulong.MaxValue),
    };

    public static Dictionary<string, BasicEnumMetadata> Apply(IEnumerable<JsonNode> roots,
        out Dictionary<string, BasicEnumMetadata> parseMetadata)
    {
        // Parsing needs every declaration name; only explicit physical values need ordinal remapping.
        var result = new Dictionary<string, BasicEnumMetadata>(StringComparer.Ordinal);
        parseMetadata = new Dictionary<string, BasicEnumMetadata>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (root is not JsonObject ro || ro["types"] is not JsonArray types) continue;
            foreach (var type in types.OfType<JsonObject>())
            {
                if (Str(type["kind"]) != "enum") continue;
                if (type["clrEnum"] is not JsonObject fact)
                {
                    // Ordinary Kotlin enums use their semantic ordinal as the Int32 literal value. Publish that
                    // producer-owned map too: a DLL consumer must not rediscover Kotlin order from FieldDef layout.
                    var ordinaryEntries = new List<BasicEnumEntry>();
                    foreach (var node in type["entries"].AsArray())
                    {
                        var entry = node.AsObject();
                        var ordinal = entry["ordinal"].GetValue<int>();
                        var value = ordinal.ToString(CultureInfo.InvariantCulture);
                        entry["underlying"] = "System.Int32";
                        entry["physicalValue"] = value;
                        ordinaryEntries.Add(new BasicEnumEntry(entry["name"].GetValue<string>(), ordinal, value));
                    }
                    StampMetadata(type, "System.Int32", ordinaryEntries);
                    parseMetadata.Add(type["name"].GetValue<string>(), new BasicEnumMetadata("System.Int32", ordinaryEntries));
                    continue;
                }
                var owner = Str(type["name"]);
                if (owner == null) Fail(type, "an explicit enum has no name");
                var kotlinUnderlying = TypeJson.OwnerName(fact["underlying"]);
                if (kotlinUnderlying == null || !Underlying.TryGetValue(kotlinUnderlying, out var physical))
                {
                    Fail(type, $"'{owner}' has an unsupported underlying Kotlin type");
                    physical = default;
                }
                var entries = type["entries"] as JsonArray;
                if (entries == null)
                    Fail(type, $"'{owner}' has no entry map");

                var ordered = new List<BasicEnumEntry>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var values = new HashSet<BigInteger>();
                for (var ordinal = 0; ordinal < entries.Count; ordinal++)
                {
                    var entry = entries[ordinal] as JsonObject;
                    var name = entry == null ? null : Str(entry["name"]);
                    var text = entry == null ? null : Str(entry["value"]);
                    var value = default(BigInteger);
                    var parsed = text != null && BigInteger.TryParse(
                        text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                    if (entry == null || name == null || !names.Add(name)
                        || Int(entry["ordinal"]) != ordinal || !parsed)
                    {
                        Fail(type, $"'{owner}' has a malformed ordered entry at index {ordinal}");
                        value = default;
                    }
                    if (value < physical.Min || value > physical.Max)
                        Fail(type, $"entry '{owner}.{name}' value {text} is outside {kotlinUnderlying}");
                    if (!values.Add(value))
                        Fail(type, $"'{owner}' has duplicate physical enum value {text}; aliases are not supported");

                    var normalized = value.ToString(CultureInfo.InvariantCulture);
                    entry.Remove("value");
                    entry["underlying"] = physical.Clr;
                    entry["physicalValue"] = normalized;
                    ordered.Add(new BasicEnumEntry(name, ordinal, normalized));
                }

                type.Remove("clrEnum");
                StampMetadata(type, physical.Clr, ordered);
                RemoveSourceMarker(type);
                var metadata = new BasicEnumMetadata(physical.Clr, ordered);
                if (!result.TryAdd(owner, metadata))
                    Fail(type, $"duplicate explicit enum declaration '{owner}'");
                parseMetadata.Add(owner, metadata);
            }
        }
        return result;
    }

    static void StampMetadata(JsonObject type, string underlying, IReadOnlyList<BasicEnumEntry> entries)
    {
        type["underlying"] = TypeJson.Fqn(underlying);
        type["basicEnum"] = new JsonObject
        {
            ["underlying"] = underlying,
            ["entries"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
            {
                ["name"] = entry.Name,
                ["ordinal"] = entry.Ordinal,
                ["physicalValue"] = entry.PhysicalValue,
            }).ToArray()),
        };
    }

    static void RemoveSourceMarker(JsonObject type)
    {
        if (type["attrs"] is not JsonArray attrs) return;
        foreach (var attr in attrs.OfType<JsonObject>().Where(attr =>
                     TypeJson.OwnerName(attr["attr"]) == "kotlin.clr.ClrEnum").ToList())
            attrs.Remove(attr);
    }

    static int? Int(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<int>(out var value) == true ? value : null;

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    [DoesNotReturn]
    static void Fail(JsonObject type, string message)
    {
        var prefix = "";
        if (type["pos"] is JsonObject pos && Str(pos["f"]) is string file)
        {
            prefix = file;
            if (Int(pos["l"]) is int line) prefix += $":{line}";
            if (Int(pos["c"]) is int column) prefix += $":{column}";
            prefix += ": ";
        }
        throw new InvalidOperationException(prefix + "bir2cir: malformed @ClrEnum BIR: " + message);
    }
}
