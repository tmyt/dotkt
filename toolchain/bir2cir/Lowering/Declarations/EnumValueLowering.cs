using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A Kotlin enum entry is semantic declaration identity: owner + entry name. Local and referenced rich Kotlin enums carry an
// explicit producer map from that identity to its singleton field; a basic local enum uses its contiguous ordinal,
// while a referenced C# enum may be sparse, negative, aliased, or UInt64-backed. Only bir2cir may translate each of
// those Kotlin identities to the selected reference DLL's physical representation.
static partial class EnumValueLowering
{
    public static IReadOnlyDictionary<string, (string Name, string Ordinal)> CollectRichInstanceFields(IEnumerable<BirFileDeclaration> roots)
    {
        var fields = new Dictionary<string, (string Name, string Ordinal)>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var type in root.Types.OrEmpty().OfType<BirClassDefinition>())
                if (type.EnumRich.OrDefault())
                    fields.Add(type.Name.Value,
                        (type.RichEnum.Value.Name.Value, type.RichEnum.Value.Ordinal.Value));
        return fields;
    }

    static bool TryRichInstanceFields(string owner, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, (string Name, string Ordinal)> localRichFields,
        out string name, out string ordinal)
    {
        if (localRichFields.TryGetValue(owner, out var fields))
        {
            name = fields.Name;
            ordinal = fields.Ordinal;
            return true;
        }
        return refs.TryKotlinRichEnumInstanceFields(owner, out name, out ordinal);
    }

    static BasicEnumMetadata ReferencedOrdinalMetadata(TypeNode enumType, ReferenceMetadataIndex refs)
    {
        if (enumType is not TypeNode.Fqn owner || !(refs.TryKotlinBasicEnum(owner.Name, out var metadata)
                || refs.TryNativeEnumEntries(owner.Name, out metadata)))
            throw new InvalidOperationException("Referenced enum ordinal has no selected declaration map");
        return metadata;
    }

    internal static BirEnumOrdinal ReferencedOrdinal(IBirExpression receiver, TypeNode enumType, ReferenceMetadataIndex refs)
        => new() {
            E = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)receiver).DeepClone()),
            Type = BirGraphValue.CopyType(enumType), Values = ValuesValue(ReferencedOrdinalMetadata(enumType, refs)),
        };

    static int _parseTemp;
}

sealed record EnumPhysicalConstant(string Underlying, string Value);
