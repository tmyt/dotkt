using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RoundtripMetadata
{
    // Neither source typealiases nor pre-plugin callable signatures describe CLR storage.
    internal static void FreezeSourceDeclarations(BirFileDeclaration file)
    {
        var aliases = file.TypeAliases.OrEmpty();
        var callables = file.SourceCallables.OrEmpty();
        var scopes = file.DslScopes.OrEmpty();
        if (aliases.Length != 0 || callables.Length != 0 || scopes.Length != 0)
        {
            var attributes = file.AssemblyAttrs.OrEmpty().ToList();
            void Add(string name, byte[] payload) => attributes.Add(new BirAttribute {
                    Attr = new TypeNode.Fqn(name),
                    ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
                    Args = new IBirAttributeValue[] {
                        new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(BirCarrier.MessagePackV1) },
                        new BirOpaqueBytes { Bytes = payload },
                    },
                });
            foreach (var alias in aliases)
                Add(AKTypeAlias, BirCarrier.EncodeBody(BirCarrier.MessagePackV1, alias,
                    SourceMetadataJson.Write, SourceMetadataMessagePack.Write));
            foreach (var callable in callables)
                Add(AKSourceCallable, BirCarrier.EncodeBody(BirCarrier.MessagePackV1, callable,
                    SourceMetadataJson.Write, SourceMetadataMessagePack.Write));
            foreach (var scope in scopes)
                Add(AKDslScope, BirCarrier.EncodeBody(BirCarrier.MessagePackV1, scope,
                    SourceMetadataJson.Write, SourceMetadataMessagePack.Write));
            file.AssemblyAttrs = attributes.ToArray();
        }
        file.TypeAliases = default;
        file.SourceCallables = default;
        file.DslScopes = default;
    }
}
