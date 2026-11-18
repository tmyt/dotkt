using System.Reflection.Metadata;
using DotKt.Klib.Metadata;
using Value = DotKt.Klib.Metadata.Annotation.Types.Argument.Types.Value;

internal sealed partial class AssemblyScanner
{
    private const string TargetAnnotation = "kotlin.annotation.Target";
    private const string TargetEnum = "kotlin.annotation.AnnotationTarget";
    private readonly Dictionary<MetadataReader, Dictionary<int, string>?> _targetEnumNames = new();

    private Annotation? KotlinAnnotationTargets(TypeDefinitionHandle owner, NameTable names)
    {
        foreach (var (type, value) in _attrs.Applications(owner, TargetAnnotation))
        {
            if (!_publicTypeCatalog.TryResolveDefinition(_md, type, out var definition, _definitionPath))
                continue;
            if (!_targetEnumNames.TryGetValue(definition.Reader, out var entries))
                _targetEnumNames[definition.Reader] = entries = TargetEntries(definition.Reader);
            if (entries is null) continue; // A foreign attribute with the same spelling is not Kotlin's @Target.
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

    private static Dictionary<int, string>? TargetEntries(MetadataReader reader)
    {
        if (!new MetadataAttributes(reader).IsStandardLibrary) return null;
        var definition = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(type =>
            reader.StringComparer.Equals(type.Namespace, "kotlin.annotation")
            && reader.StringComparer.Equals(type.Name, "AnnotationTarget") && type.GetDeclaringType().IsNil);
        return definition.GetFields().Select(reader.GetFieldDefinition)
            .Where(field => !field.GetDefaultValue().IsNil)
            .ToDictionary(field => reader.GetBlobReader(reader.GetConstant(field.GetDefaultValue()).Value).ReadInt32(),
                field => reader.GetString(field.Name));
    }
}
