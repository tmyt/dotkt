using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using MessagePack;

static partial class RoundtripMetadata
{
    internal static byte[] VisibilityContent(string visibility) => BirCarrier.EncodeMetadata(visibility,
        static (ref MessagePackWriter writer, string value) => {
            writer.WriteMapHeader(1); writer.Write("visibility"); IrMessagePack.WriteString(ref writer, value);
        });

    static byte[] PropertyStorageContent(string owner, string field) => BirCarrier.EncodeMetadata(
        (owner, field), static (ref MessagePackWriter writer, (string owner, string field) value) => {
            writer.WriteMapHeader(2); writer.Write("owner"); IrMessagePack.WriteString(ref writer, value.owner);
            writer.Write("field"); IrMessagePack.WriteString(ref writer, value.field);
        });

    static byte[] ExtensionCoreContent(string name) => BirCarrier.EncodeMetadata(name,
        static (ref MessagePackWriter writer, string value) => {
            writer.WriteMapHeader(1); writer.Write("name"); IrMessagePack.WriteString(ref writer, value);
        });

    static byte[] SourceMethodIdentityContent(string name, string declaration) => BirCarrier.EncodeMetadata(
        (name, declaration), static (ref MessagePackWriter writer, (string name, string declaration) value) => {
            writer.WriteMapHeader(value.declaration == null ? 1 : 2);
            writer.Write("name"); IrMessagePack.WriteString(ref writer, value.name);
            if (value.declaration != null) { writer.Write("sourceDeclaration"); IrMessagePack.WriteString(ref writer, value.declaration); }
        });

    static BirAttribute PlacementCarrier(string name, byte[] content, string codec = BirCarrier.MessagePackV1) => new() {
        Attr = new TypeNode.Fqn(name),
        ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.String"), new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
        Args = new IBirAttributeValue[] {
            new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(codec) },
            new BirOpaqueBytes { Bytes = content },
        },
    };

    public static void PreserveTypeVisibility(IBirTypeDeclaration type) => type.Attrs = type.Attrs.OrEmpty()
        .Append(PlacementCarrier(AKTypeVisibility, VisibilityContent(type.Vis.Value))).ToArray();

    internal static void StampPropertyStorage(BirFunctionDeclaration getter, string owner, string field)
        => getter.Attrs = getter.Attrs.OrEmpty().Append(PlacementCarrier(AKPropertyStorage, PropertyStorageContent(owner, field))).ToArray();

    internal static void StampProjectedMethod(BirFunctionDeclaration method, ProjectedMethodPlan plan)
        => method.Attrs = method.Attrs.OrEmpty().Append(PlacementCarrier(AKProjectedMethod,
            BirCarrier.EncodeMetadata(plan, static (ref MessagePackWriter writer, ProjectedMethodPlan p) => {
                writer.WriteMapHeader(3); writer.Write("facets");
                writer.WriteArrayHeader(p.Facets.Length);
                foreach (var f in p.Facets)
                {
                    writer.WriteArrayHeader(3); writer.Write(f.OwnerIndex);
                    TypeNodeMessagePack.Write(ref writer, f.Bound); writer.Write(f.NativeArgument);
                }
                writer.Write("addresses"); writer.WriteArrayHeader(p.AddressOperands.Length);
                foreach (var operand in p.AddressOperands) writer.Write(operand);
                writer.Write("nativeArguments"); writer.WriteArrayHeader(p.NativeIndices.Length);
                foreach (var index in p.NativeIndices) writer.Write(index);
            }))).ToArray();

    internal static void StampProjectedConstructor(BirFunctionDeclaration factory, ProjectedConstructorPlan plan)
        => factory.Attrs = factory.Attrs.OrEmpty().Append(PlacementCarrier(AKProjectedConstructor,
            BirCarrier.EncodeMetadata(plan, static (ref MessagePackWriter writer, ProjectedConstructorPlan p) => {
                writer.WriteMapHeader(3);
                writer.Write("primary"); writer.Write(p.PrimaryArity);
                writer.Write("captured"); writer.Write(p.Captured);
                writer.Write("facets"); writer.WriteArrayHeader(p.Facets.Length);
                foreach (var f in p.Facets)
                {
                    writer.WriteArrayHeader(3); writer.Write(f.Primary);
                    TypeNodeMessagePack.Write(ref writer, f.Bound); writer.Write(f.NativeArgument);
                }
            }))).ToArray();

    internal static void StampExtensionCore(BirFunctionDeclaration wrapper, string core)
        => wrapper.Attrs = wrapper.Attrs.OrEmpty().Append(PlacementCarrier(AKExtensionCore, ExtensionCoreContent(core))).ToArray();

    internal static void AddSourceMethodIdentity(BirFunctionDeclaration method, string sourceName,
        string sourceDeclaration = null) => method.Attrs = method.Attrs.OrEmpty()
            .Append(PlacementCarrier(AKSourceMethod, SourceMethodIdentityContent(sourceName, sourceDeclaration))).ToArray();

    internal static void CopySourceOverloadPriority(BirFunctionDeclaration source, BirFunctionDeclaration target)
    {
        foreach (var attribute in source.Attrs.OrEmpty())
            if (attribute.Attr.OrDefault() is TypeNode.Fqn { Name: SourceLowPriority })
                target.Attrs = target.Attrs.OrEmpty().Append((BirAttribute)attribute.DeepClone()).ToArray();
    }

    internal static void CopySourceMethodIdentity(BirFunctionDeclaration source, BirFunctionDeclaration target)
    {
        if (target.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: AKSourceMethod })) return;
        foreach (var attribute in source.Attrs.OrEmpty())
            if (attribute.Attr.OrDefault() is TypeNode.Fqn { Name: AKSourceMethod })
                target.Attrs = target.Attrs.OrEmpty().Append((BirAttribute)attribute.DeepClone()).ToArray();
    }
}
