using System.Globalization;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed record BasicEnumEntry(string Name, int Ordinal, string PhysicalValue);
sealed record BasicEnumMetadata(string Underlying, IReadOnlyList<BasicEnumEntry> Entries);

// BASIC ENUM DECLARATION LOWERING. kotc has already selected and source-validated the exact Kotlin contract:
// BIR carries ordered Kotlin enum declarations; @ClrEnum additionally supplies an underlying type and constants. This is the
// Kotlin-to-CLR boundary: validate that producer fact defensively, select the legal CLR underlying type, author the
// physical literal values, and retain the ordered map for operations and trusted round-trip metadata.
static partial class ClrEnumLowering
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

    public static Dictionary<string, BasicEnumMetadata> Apply(IEnumerable<BirFileDeclaration> roots,
        out Dictionary<string, BasicEnumMetadata> parseMetadata)
    {
        // Parsing needs every declaration name; only explicit physical values need ordinal remapping.
        var result = new Dictionary<string, BasicEnumMetadata>(StringComparer.Ordinal);
        parseMetadata = new Dictionary<string, BasicEnumMetadata>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var type in root.Types.OrEmpty().OfType<BirEnumDefinition>())
            {
                if (type.Kind.OrDefault() != "enum") continue;
                if (!type.ClrEnum.HasValue)
                {
                    // Ordinary Kotlin enums use their semantic ordinal as the Int32 literal value. Publish that
                    // producer-owned map too: a DLL consumer must not rediscover Kotlin order from FieldDef layout.
                    var ordinaryEntries = new List<BasicEnumEntry>();
                    foreach (var entry in type.Entries.Value)
                    {
                        var ordinal = entry.Ordinal.Value;
                        var value = ordinal.ToString(CultureInfo.InvariantCulture);
                        entry.Underlying = "System.Int32";
                        entry.PhysicalValue = value;
                        ordinaryEntries.Add(new BasicEnumEntry(entry.Name.Value, ordinal, value));
                    }
                    StampMetadata(type, "System.Int32", ordinaryEntries);
                    parseMetadata.Add(type.Name.Value, new BasicEnumMetadata("System.Int32", ordinaryEntries));
                    continue;
                }
                var owner = type.Name.OrDefault();
                if (owner == null) Fail(type, "an explicit enum has no name");
                var kotlinUnderlying = (type.ClrEnum.Value.Underlying.OrDefault() as TypeNode.Fqn)?.Name;
                if (kotlinUnderlying == null || !Underlying.TryGetValue(kotlinUnderlying, out var physical))
                {
                    Fail(type, $"'{owner}' has an unsupported underlying Kotlin type");
                    physical = default;
                }
                var entries = type.Entries.OrDefault();
                if (entries == null)
                    Fail(type, $"'{owner}' has no entry map");

                var ordered = new List<BasicEnumEntry>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var values = new HashSet<BigInteger>();
                for (var ordinal = 0; ordinal < entries.Length; ordinal++)
                {
                    var entry = entries[ordinal];
                    var name = entry?.Name.OrDefault();
                    var text = entry?.Value.OrDefault();
                    var value = default(BigInteger);
                    var parsed = text != null && BigInteger.TryParse(
                        text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                    if (entry == null || name == null || !names.Add(name)
                        || !entry.Ordinal.HasValue || entry.Ordinal.Value != ordinal || !parsed)
                    {
                        Fail(type, $"'{owner}' has a malformed ordered entry at index {ordinal}");
                        value = default;
                    }
                    if (value < physical.Min || value > physical.Max)
                        Fail(type, $"entry '{owner}.{name}' value {text} is outside {kotlinUnderlying}");
                    if (!values.Add(value))
                        Fail(type, $"'{owner}' has duplicate physical enum value {text}; aliases are not supported");

                    var normalized = value.ToString(CultureInfo.InvariantCulture);
                    entry.Value = default;
                    entry.Underlying = physical.Clr;
                    entry.PhysicalValue = normalized;
                    ordered.Add(new BasicEnumEntry(name, ordinal, normalized));
                }

                type.ClrEnum = default;
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

    static void StampMetadata(BirEnumDefinition type, string underlying, IReadOnlyList<BasicEnumEntry> entries)
    {
        type.Underlying = new TypeNode.Fqn(underlying);
        type.BasicEnum = new BirBasicEnumCarrier
        {
            Underlying = underlying,
            Entries = entries.Select(entry => new BirBasicEnumCarrierEntry
            {
                Name = entry.Name,
                Ordinal = entry.Ordinal,
                PhysicalValue = entry.PhysicalValue,
            }).ToArray(),
        };
    }

    static void RemoveSourceMarker(BirEnumDefinition type)
    {
        if (!type.Attrs.HasValue) return;
        type.Attrs = type.Attrs.Value.Where(attr =>
            attr.Attr.OrDefault() is not TypeNode.Fqn { Name: "kotlin.clr.ClrEnum" }).ToArray();
    }

    [DoesNotReturn]
    static void Fail(BirEnumDefinition type, string message)
    {
        var prefix = "";
        if (type.Pos.OrDefault() is { } pos && pos.F.OrDefault() is string file)
        {
            prefix = file;
            if (pos.L.HasValue) prefix += $":{pos.L.Value}";
            if (pos.C.HasValue) prefix += $":{pos.C.Value}";
            prefix += ": ";
        }
        throw new InvalidOperationException(prefix + "bir2cir: malformed @ClrEnum BIR: " + message);
    }
}
