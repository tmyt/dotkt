using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrEnumLowering
{
    internal static void SelfTest()
    {
        var ordinaryFirst = new BirEnumEntryDeclaration { Name = "FIRST", Ordinal = 0 };
        var ordinarySecond = new BirEnumEntryDeclaration { Name = "SECOND", Ordinal = 1 };
        var ordinary = new BirEnumDefinition {
            Kind = "enum", Name = "Ordinary", Entries = new[] { ordinaryFirst, ordinarySecond },
            ClrEnum = OptionalField<BirClrEnumDeclaration>.Null,
            Attrs = OptionalField<BirAttribute[]>.Null,
        };
        var explicitFirst = new BirEnumEntryDeclaration { Name = "LATE", Ordinal = 0, Value = "+0009" };
        var explicitSecond = new BirEnumEntryDeclaration { Name = "EARLY", Ordinal = 1, Value = "-2" };
        var retainedAttribute = new BirAttribute { Attr = new TypeNode.Fqn("Test.Keep") };
        var physical = new BirEnumDefinition {
            Kind = "enum", Name = "Physical", Entries = new[] { explicitFirst, explicitSecond },
            ClrEnum = new BirClrEnumDeclaration { Underlying = new TypeNode.Fqn("kotlin.Short") },
            Attrs = new[] {
                new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrEnum") }, retainedAttribute,
                new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrEnum") },
            },
        };
        var wideEntry = new BirEnumEntryDeclaration { Name = "MAX", Ordinal = 0, Value = "18446744073709551615" };
        var wide = new BirEnumDefinition {
            Kind = "enum", Name = "Wide", Entries = new[] { wideEntry },
            ClrEnum = new BirClrEnumDeclaration { Underlying = new TypeNode.Fqn("kotlin.ULong") },
        };
        var first = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { ordinary, physical } };
        var second = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { wide } };
        var explicitMaps = Apply(new[] { first, second }, out var parseMaps);

        if (explicitMaps.Count != 2 || parseMaps.Count != 3 || explicitMaps.ContainsKey("Ordinary")
            || !explicitMaps.Keys.SequenceEqual(new[] { "Physical", "Wide" })
            || !parseMaps.Keys.SequenceEqual(new[] { "Ordinary", "Physical", "Wide" })
            || !parseMaps["Ordinary"].Entries.SequenceEqual(new[] {
                new BasicEnumEntry("FIRST", 0, "0"), new BasicEnumEntry("SECOND", 1, "1"),
            })
            || !explicitMaps["Physical"].Entries.SequenceEqual(new[] {
                new BasicEnumEntry("LATE", 0, "9"), new BasicEnumEntry("EARLY", 1, "-2"),
            })
            || explicitMaps["Wide"].Entries.Single().PhysicalValue != "18446744073709551615")
            throw new InvalidOperationException("Typed enum declaration lowering changed its ordered physical maps");

        if (!ReferenceEquals(first.Types.Value[0], ordinary) || !ReferenceEquals(first.Types.Value[1], physical)
            || !ReferenceEquals(physical.Entries.Value[0], explicitFirst)
            || !ReferenceEquals(physical.Entries.Value[1], explicitSecond)
            || physical.Underlying.Value != new TypeNode.Fqn("System.Int16")
            || ordinary.Underlying.Value != new TypeNode.Fqn("System.Int32")
            || wide.Underlying.Value != new TypeNode.Fqn("System.UInt64")
            || ordinaryFirst.Underlying.Value != "System.Int32" || ordinarySecond.PhysicalValue.Value != "1"
            || explicitFirst.Underlying.Value != "System.Int16" || explicitSecond.PhysicalValue.Value != "-2"
            || explicitFirst.Value.IsPresent || explicitSecond.Value.IsPresent || physical.ClrEnum.IsPresent
            || wideEntry.Value.IsPresent || wide.ClrEnum.IsPresent
            || !ordinary.ClrEnum.IsNull || !ordinary.Attrs.IsNull || wide.Attrs.IsPresent
            || physical.Attrs.Value.Length != 1 || !ReferenceEquals(physical.Attrs.Value[0], retainedAttribute))
            throw new InvalidOperationException("Typed enum declaration lowering lost source identity or fact presence");

        foreach (var type in new[] { ordinary, physical, wide })
        {
            var metadata = parseMaps[type.Name.Value];
            var carrier = type.BasicEnum.Value;
            if (carrier.Underlying.Value != metadata.Underlying
                || !carrier.Entries.Value.Select(entry => new BasicEnumEntry(entry.Name.Value, entry.Ordinal.Value,
                    entry.PhysicalValue.Value)).SequenceEqual(metadata.Entries))
                throw new InvalidOperationException("Typed basic enum carrier lost the producer's exact physical map");
        }
        if (!EnumMemberBinding.CollectBasicEnums(new[] { first, second }).SetEquals(parseMaps.Keys))
            throw new InvalidOperationException("Typed basic enum inventory lost cross-file declarations");
        Console.WriteLine("[typed enum declarations] self-test OK (ordered maps, native constants, identity and presence)");
    }
}
