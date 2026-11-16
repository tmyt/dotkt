using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    internal static void SourceTypeAliasSelfTest()
    {
        var expanded = new SourceType.ClassType(new SourceClassifier("kotlin", Array.Empty<string>(), "String"),
            true, false, false, Array.Empty<SourceTypeArgument>(), Array.Empty<SourceAnnotation>());
        var aliases = new[] {
            new SourceTypeAlias("sample", Array.Empty<string>(), "Text", "public", Array.Empty<SourceTypeParameter>(),
                expanded, expanded, Array.Empty<SourceAnnotation>()),
            new SourceTypeAlias("sample", new[] { "Owner" }, "NestedText", "internal", Array.Empty<SourceTypeParameter>(),
                expanded, expanded, Array.Empty<SourceAnnotation>()),
        };
        var preceding = new BirAttribute { Attr = new TypeNode.Fqn("Example.Attribute") };
        var file = new BirFileDeclaration { FileClass = "AliasFile", TypeAliases = aliases, AssemblyAttrs = new[] { preceding } };
        FreezeTypeAliases(file);
        if (file.TypeAliases.IsPresent || file.FileClass.Value != "AliasFile" || file.AssemblyAttrs.Value.Length != 3
            || !ReferenceEquals(file.AssemblyAttrs.Value[0], preceding))
            throw new InvalidOperationException("Alias freeze changed its owner, prior attributes, order or source-fact lifetime");
        for (var index = 0; index < aliases.Length; index++)
        {
            var attribute = file.AssemblyAttrs.Value[index + 1];
            if (attribute.Attr.Value is not TypeNode.Fqn { Name: AKTypeAlias }
                || attribute.ArgTypes.Value is not [TypeNode.Fqn { Name: "System.String" }, TypeNode.Array { Elem: TypeNode.Fqn { Name: "System.Byte" } }]
                || attribute.Args.Value is not [BirAttributeLiteral { Value.Value: BirLiteral.TextLiteral { Value: BirCarrier.MessagePackV1 } }, BirOpaqueBytes bytes])
                throw new InvalidOperationException("Alias freeze changed its carrier constructor or envelope");
            var expected = BirCarrier.EncodeBody(BirCarrier.MessagePackV1, aliases[index], SourceMetadataJson.Write, SourceMetadataMessagePack.Write);
            var decoded = BirCarrier.DecodeBody(BirCarrier.MessagePackV1, bytes.Bytes.Value, SourceMetadataJson.Read, SourceMetadataMessagePack.Read);
            if (!bytes.Bytes.Value.SequenceEqual(expected) || !SourceMetadataDataEquality.Equal(decoded, aliases[index]))
                throw new InvalidOperationException("Alias freeze changed source facts or the existing shared payload encoding");
        }
        var frozen = file.AssemblyAttrs.Value;
        FreezeTypeAliases(file);
        if (!ReferenceEquals(file.AssemblyAttrs.Value, frozen))
            throw new InvalidOperationException("Consumed aliases were frozen a second time");
        foreach (var presence in new[] { default(OptionalField<BirAttribute[]>), OptionalField<BirAttribute[]>.Null,
            OptionalField<BirAttribute[]>.Of(Array.Empty<BirAttribute>()) })
        {
            var empty = new BirFileDeclaration { TypeAliases = Array.Empty<SourceTypeAlias>(), AssemblyAttrs = presence };
            FreezeTypeAliases(empty);
            if (empty.TypeAliases.IsPresent || empty.AssemblyAttrs.IsPresent != presence.IsPresent
                || empty.AssemblyAttrs.IsNull != presence.IsNull
                || presence.HasValue && !ReferenceEquals(empty.AssemblyAttrs.Value, presence.Value))
                throw new InvalidOperationException("Empty aliases changed assembly attribute presence");
        }
        Console.WriteLine("[typed typealias freeze] self-test OK (carrier bytes, declaration order, presence and source consumption)");
    }
}
