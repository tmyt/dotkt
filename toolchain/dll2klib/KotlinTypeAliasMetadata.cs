using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotKt.Bir;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;
using KValue = DotKt.Klib.Metadata.Annotation.Types.Argument.Types.Value;

// Source declaration facts, not CLR signature decoding or executable generic frames.
internal sealed class KotlinTypeAliasMetadata
{
    internal const string AttributeName = MetadataAttributes.DotKtNs + "KotlinTypeAliasAttribute";
    readonly IReadOnlyList<SourceTypeAlias> _declarations;

    public KotlinTypeAliasMetadata(MetadataReader reader, MetadataAttributes attributes)
    {
        attributes.ValidateCarrierTargets(AttributeName, HandleKind.AssemblyDefinition);
        _declarations = reader.IsAssembly
            ? attributes.CarrierPayloads(MetadataTokens.EntityHandle(0x20000001), AttributeName)
                .Select(payload => BirCarrier.DecodeBody(payload.Codec, payload.Content,
                    SourceMetadataJson.Read, SourceMetadataMessagePack.Read)).ToArray()
            : [];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in _declarations)
            if (!identities.Add(Identity(declaration)))
                throw new InvalidDataException($"duplicate Kotlin typealias declaration '{Identity(declaration)}'");
    }

    public IEnumerable<string> Packages => _declarations.Select(declaration => declaration.PackageName);

    public void Project(string packageName, PackageFragment fragment, NameTable names)
    {
        var owners = fragment.Class.ToDictionary(klass => names.ClassName(klass.FqName)!, StringComparer.Ordinal);
        foreach (var declaration in _declarations.Where(declaration => declaration.PackageName == packageName))
        {
            var alias = new TypeAlias {
                Name = names.String(declaration.Name),
                Flags = Visibility(declaration.Visibility),
                UnderlyingType = Type(declaration.UnderlyingType, names),
                ExpandedType = Type(declaration.ExpandedType, names),
            };
            foreach (var parameter in declaration.TypeParameters)
            {
                var projected = new TypeParameter {
                    Id = parameter.Id, Name = names.String(parameter.Name),
                    Variance = parameter.Variance switch {
                        "inv" => TypeParameter.Types.Variance.Inv,
                        "in" => TypeParameter.Types.Variance.In,
                        "out" => TypeParameter.Types.Variance.Out,
                        _ => throw new InvalidDataException("invalid typealias parameter variance"),
                    },
                };
                projected.UpperBound.Add(parameter.Bounds.Select(bound => Type(bound, names)));
                projected.TypeParameterAnnotation.Add(parameter.Annotations.Select(annotation => ReadAnnotation(annotation, names)));
                alias.TypeParameter.Add(projected);
            }
            alias.Annotation.Add(declaration.Annotations.Select(annotation => ReadAnnotation(annotation, names)));
            if (alias.Annotation.Count > 0) alias.Flags |= 1;
            if (declaration.OwnerPath.Length == 0) fragment.Package.TypeAlias.Add(alias);
            else if (owners.TryGetValue(string.Join('.', declaration.OwnerPath.Prepend(packageName).Where(part => part.Length > 0)), out var owner))
                owner.TypeAlias.Add(alias);
            // A source-invisible containing class is absent from the reference KLIB, including its aliases.
        }
    }

    static KType Type(SourceType source, NameTable names)
    {
        var result = new KType {
            Nullable = source.Nullable,
            Flags = (source.Suspend ? 1 : 0) | (source.DefinitelyNotNull ? 2 : 0),
        };
        switch (source)
        {
            case SourceType.ClassType node: result.ClassName = Classifier(node.Classifier, names); break;
            case SourceType.AliasType node: result.TypeAliasName = Classifier(node.Classifier, names); break;
            case SourceType.ParameterType node: result.TypeParameter = node.Id; break;
            default: throw new FormatException();
        }
        foreach (var argument in source.Arguments)
        {
            var projected = argument switch {
                SourceTypeArgument.InvArgument node => new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv, Type = Type(node.Type, names) },
                SourceTypeArgument.InArgument node => new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.In, Type = Type(node.Type, names) },
                SourceTypeArgument.OutArgument node => new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Out, Type = Type(node.Type, names) },
                SourceTypeArgument.StarArgument => new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Star },
                _ => throw new FormatException(),
            };
            result.Argument.Add(projected);
        }
        result.TypeAnnotation.Add(source.Annotations.Select(annotation => ReadAnnotation(annotation, names)));
        if (source.OuterType is { } outer) result.OuterType = Type(outer, names);
        if (source.AbbreviatedType is { } abbreviated) result.AbbreviatedType = Type(abbreviated, names);
        if (source.FlexibleUpperBound is { } flexible)
        {
            result.FlexibleUpperBound = Type(flexible, names);
            result.FlexibleTypeCapabilitiesId = names.String(source.FlexibleCapabilities!);
        }
        return result;
    }

    static Annotation ReadAnnotation(SourceAnnotation source, NameTable names)
    {
        var result = new Annotation { Id = Classifier(source.Classifier, names) };
        foreach (var argument in source.Arguments)
            result.Argument.Add(new Annotation.Types.Argument {
                NameId = names.String(argument.Name), Value = Value(argument.Value, names),
            });
        return result;
    }

    static KValue Value(SourceAnnotationValue source, NameTable names)
    {
        var result = new KValue { Flags = source.Unsigned ? 1 : 0 };
        switch (source)
        {
            case SourceAnnotationValue.ByteValue node:
                result.Type = KValue.Types.Type.Byte; result.IntValue = node.Value; break;
            case SourceAnnotationValue.CharValue node:
                result.Type = KValue.Types.Type.Char; result.IntValue = node.Value; break;
            case SourceAnnotationValue.ShortValue node:
                result.Type = KValue.Types.Type.Short; result.IntValue = node.Value; break;
            case SourceAnnotationValue.IntValue node:
                result.Type = KValue.Types.Type.Int; result.IntValue = node.Value; break;
            case SourceAnnotationValue.LongValue node:
                result.Type = KValue.Types.Type.Long; result.IntValue = node.Value; break;
            case SourceAnnotationValue.BooleanValue node:
                result.Type = KValue.Types.Type.Boolean; result.IntValue = node.Value; break;
            case SourceAnnotationValue.FloatValue node:
                result.Type = KValue.Types.Type.Float; result.FloatValue = BitConverter.Int32BitsToSingle(checked((int)node.Bits)); break;
            case SourceAnnotationValue.DoubleValue node:
                result.Type = KValue.Types.Type.Double; result.DoubleValue = BitConverter.Int64BitsToDouble(node.Bits); break;
            case SourceAnnotationValue.StringValue node:
                result.Type = KValue.Types.Type.String; result.StringValue = names.String(node.Value); break;
            case SourceAnnotationValue.ClassValue node:
                result.Type = KValue.Types.Type.Class; result.ClassId = Classifier(node.Classifier, names);
                result.ArrayDimensionCount = node.ArrayDimensions; break;
            case SourceAnnotationValue.EnumValue node:
                result.Type = KValue.Types.Type.Enum; result.ClassId = Classifier(node.Classifier, names);
                result.EnumValueId = names.String(node.Name); break;
            case SourceAnnotationValue.AnnotationValue node:
                result.Type = KValue.Types.Type.Annotation; result.Annotation = ReadAnnotation(node.Value, names); break;
            case SourceAnnotationValue.ArrayValue node:
                result.Type = KValue.Types.Type.Array; result.ArrayElement.Add(node.Value.Select(value => Value(value, names))); break;
            default: throw new FormatException();
        }
        return result;
    }

    static int Classifier(SourceClassifier source, NameTable names) =>
        names.Class(source.PackageName, source.OwnerPath.Append(source.Name));
    static string Identity(SourceTypeAlias source) => string.Join('.', source.OwnerPath
        .Prepend(source.PackageName).Append(source.Name).Where(part => part.Length > 0));
    static int Visibility(string visibility) => visibility switch {
        "internal" => 0, "private" => 2, "protected" => 4, "public" => 6, "private_to_this" => 8, "local" => 10,
        _ => throw new InvalidDataException("invalid Kotlin typealias visibility"),
    };
}
