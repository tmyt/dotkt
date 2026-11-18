using System.Collections.Concurrent;
using System.Reflection.Metadata;
using DotKt.Klib.Metadata;
using Value = DotKt.Klib.Metadata.Annotation.Types.Argument.Types.Value;

internal sealed partial class AssemblyScanner
{
    private const string TargetAnnotation = "kotlin.annotation.Target";
    private const string TargetEnum = "kotlin.annotation.AnnotationTarget";

    private Annotation? KotlinAnnotationTargets(TypeDefinitionHandle owner, NameTable names)
    {
        foreach (var (type, ctor, value) in _attrs.Applications(owner, TargetAnnotation))
        {
            if (!_publicTypeCatalog.TryResolveDefinition(_md, type, out var definition, _definitionPath))
                continue;
            var enumHandle = TargetParameter(_md, ctor);
            if (enumHandle.IsNil || !_publicTypeCatalog.TryResolveDefinition(_md, enumHandle, out var enumeration, _definitionPath)
                || definition.DefinitionPath != enumeration.DefinitionPath) continue;
            var entries = _publicTypeCatalog.KotlinTargetEntries(definition, enumeration);
            if (entries is null) continue;
            var blob = _md.GetBlobReader(value);
            if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid Kotlin @Target prolog");
            var count = blob.ReadInt32();
            if (count < 0 || count > (blob.RemainingBytes - 2) / sizeof(int))
                throw new BadImageFormatException("Invalid Kotlin @Target array");
            var array = new Value { Type = Value.Types.Type.Array };
            for (var index = 0; index < count; ++index)
                array.ArrayElement.Add(new Value { Type = Value.Types.Type.Enum, ClassId = names.Class(TargetEnum),
                    EnumValueId = names.String(entries[blob.ReadInt32()]) });
            if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
                throw new BadImageFormatException("Unexpected Kotlin @Target arguments");
            return new Annotation { Id = names.Class(TargetAnnotation), Argument = {
                new Annotation.Types.Argument { NameId = names.String("allowedTargets"), Value = array } } };
        }
        return null;
    }

    // A custom-attribute blob has no parameter type tags. Decode its integers
    // only for an actual instance .ctor(enum[]) in the resolved stdlib.
    private static EntityHandle TargetParameter(MetadataReader reader, EntityHandle constructor)
    {
        var (name, signature) = constructor.Kind switch {
            HandleKind.MethodDefinition => (reader.GetMethodDefinition((MethodDefinitionHandle)constructor).Name,
                reader.GetMethodDefinition((MethodDefinitionHandle)constructor).Signature),
            HandleKind.MemberReference => (reader.GetMemberReference((MemberReferenceHandle)constructor).Name,
                reader.GetMemberReference((MemberReferenceHandle)constructor).Signature),
            _ => default,
        };
        if (!reader.StringComparer.Equals(name, ".ctor")) return default;
        var blob = reader.GetBlobReader(signature);
        var header = blob.ReadSignatureHeader();
        if (!header.IsInstance || header.IsGeneric || header.CallingConvention != SignatureCallingConvention.Default
            || blob.ReadCompressedInteger() != 1 || blob.ReadSignatureTypeCode() != SignatureTypeCode.Void
            || blob.ReadSignatureTypeCode() != SignatureTypeCode.SZArray
            || blob.ReadByte() != (byte)SignatureTypeKind.ValueType) return default;
        var element = blob.ReadTypeHandle();
        return blob.RemainingBytes == 0 ? element : default;
    }
}

internal sealed partial class PublicTypeCatalog
{
    // One cache for the entire resolved reference universe, shared by package
    // workers and input assemblies. Never cache an application or its target list.
    private readonly ConcurrentDictionary<(MetadataReader, TypeDefinitionHandle, TypeDefinitionHandle),
        Lazy<Dictionary<int, string>?>> _kotlinTargets = new();

    internal Dictionary<int, string>? KotlinTargetEntries(ResolvedTypeDefinition target, ResolvedTypeDefinition enumeration)
        => _kotlinTargets.GetOrAdd((target.Reader, target.Handle, enumeration.Handle),
            _ => new(() => ReadTargetEntries(target, enumeration))).Value;

    private Dictionary<int, string>? ReadTargetEntries(ResolvedTypeDefinition target, ResolvedTypeDefinition enumeration)
    {
        var reader = target.Reader;
        var definition = reader.GetTypeDefinition(enumeration.Handle);
        if (!reader.StringComparer.Equals(definition.Namespace, "kotlin.annotation")
            || !reader.StringComparer.Equals(definition.Name, "AnnotationTarget") || !definition.GetDeclaringType().IsNil)
            return null;
        var metadata = new MetadataAttributes(reader);
        if (!TryResolveDefinition(reader, reader.GetTypeDefinition(target.Handle).BaseType,
                out var attributeBase, target.DefinitionPath)) return null;
        // The library marker must come from the framework that declares System.Attribute.
        var baseDef = attributeBase.Reader.GetTypeDefinition(attributeBase.Handle);
        if (!attributeBase.Reader.StringComparer.Equals(baseDef.Namespace, "System")
            || !attributeBase.Reader.StringComparer.Equals(baseDef.Name, "Attribute")) return null;
        if (!metadata.HasStandardLibraryMarker(type => TryResolveDefinition(reader, type, out var marker, target.DefinitionPath)
                && marker.DefinitionPath == attributeBase.DefinitionPath))
            return null;
        return definition.GetFields().Select(reader.GetFieldDefinition)
            .Where(field => !field.GetDefaultValue().IsNil)
            .ToDictionary(field => reader.GetBlobReader(reader.GetConstant(field.GetDefaultValue()).Value).ReadInt32(),
                field => reader.GetString(field.Name));
    }

}
