using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // Alias declarations become opaque assembly metadata at the source frontier,
    // not executable CLR type slots. Consume their source vector at this boundary.
    internal static void FreezeTypeAliases(BirFileDeclaration file)
    {
        var aliases = file.TypeAliases.OrEmpty();
        if (aliases.Length != 0)
        {
            var attributes = file.AssemblyAttrs.OrEmpty().ToList();
            foreach (var alias in aliases)
                attributes.Add(new BirAttribute {
                    Attr = new TypeNode.Fqn(AKTypeAlias),
                    ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
                    Args = new IBirAttributeValue[] {
                        new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(BirCarrier.MessagePackV1) },
                        new BirOpaqueBytes { Bytes = BirCarrier.EncodeBody(BirCarrier.MessagePackV1, alias,
                            SourceMetadataJson.Write, SourceMetadataMessagePack.Write) },
                    },
                });
            file.AssemblyAttrs = attributes.ToArray();
        }
        file.TypeAliases = default;
    }
}
