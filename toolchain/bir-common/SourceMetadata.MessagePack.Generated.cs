// Generated from docs/kotlin-typealias.schema.json; run scripts/generate-ir-model.py.
#nullable enable
using System;
using System.Collections.Generic;
using MessagePack;
namespace DotKt.Bir;

public static class SourceMetadataMessagePack
{
    public static SourceTypeAlias Read(ref MessagePackReader reader) => ReadSourceTypeAlias(ref reader);
    public static void Write(ref MessagePackWriter writer, SourceTypeAlias value) => WriteSourceTypeAlias(ref writer, value, 0);

    private static SourceTypeAlias ReadSourceTypeAlias(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        string? packageName = default;
        string[]? ownerPath = default;
        string? name = default;
        string? visibility = default;
        SourceTypeParameter[]? typeParameters = default;
        SourceType? underlyingType = default;
        SourceType? expandedType = default;
        SourceAnnotation[]? annotations = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "packageName":
                    Seen(ref seen, 1UL);
                    packageName = ReadText(ref reader);
                    break;
                case "ownerPath":
                    Seen(ref seen, 2UL);
                    ownerPath = ReadArray(ref reader, ReadText);
                    break;
                case "name":
                    Seen(ref seen, 4UL);
                    name = ReadText(ref reader);
                    break;
                case "visibility":
                    Seen(ref seen, 8UL);
                    visibility = ReadText(ref reader);
                    break;
                case "typeParameters":
                    Seen(ref seen, 16UL);
                    typeParameters = ReadArray(ref reader, ReadSourceTypeParameter);
                    break;
                case "underlyingType":
                    Seen(ref seen, 32UL);
                    underlyingType = ReadSourceType(ref reader);
                    break;
                case "expandedType":
                    Seen(ref seen, 64UL);
                    expandedType = ReadSourceType(ref reader);
                    break;
                case "annotations":
                    Seen(ref seen, 128UL);
                    annotations = ReadArray(ref reader, ReadSourceAnnotation);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 255UL) != 255UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeAlias(packageName!, ownerPath!, name!, visibility!, typeParameters!, underlyingType!, expandedType!, annotations!);
    }

    private static void WriteSourceTypeAlias(ref MessagePackWriter writer, SourceTypeAlias value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(8);
        writer.Write("packageName");
        IrMessagePack.WriteString(ref writer, value.PackageName);
        writer.Write("ownerPath");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.OwnerPath.Length);
        foreach (var item in value.OwnerPath)
        {
            IrMessagePack.WriteString(ref writer, item);
        }
        writer.Write("name");
        IrMessagePack.WriteString(ref writer, value.Name);
        writer.Write("visibility");
        IrMessagePack.WriteString(ref writer, value.Visibility);
        writer.Write("typeParameters");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.TypeParameters.Length);
        foreach (var item in value.TypeParameters)
        {
            WriteSourceTypeParameter(ref writer, item, depth + 2);
        }
        writer.Write("underlyingType");
        WriteSourceType(ref writer, value.UnderlyingType, depth + 1);
        writer.Write("expandedType");
        WriteSourceType(ref writer, value.ExpandedType, depth + 1);
        writer.Write("annotations");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Annotations.Length);
        foreach (var item in value.Annotations)
        {
            WriteSourceAnnotation(ref writer, item, depth + 2);
        }
    }

    private static SourceClassifier ReadSourceClassifier(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        string? packageName = default;
        string[]? ownerPath = default;
        string? name = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "packageName":
                    Seen(ref seen, 1UL);
                    packageName = ReadText(ref reader);
                    break;
                case "ownerPath":
                    Seen(ref seen, 2UL);
                    ownerPath = ReadArray(ref reader, ReadText);
                    break;
                case "name":
                    Seen(ref seen, 4UL);
                    name = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceClassifier(packageName!, ownerPath!, name!);
    }

    private static void WriteSourceClassifier(ref MessagePackWriter writer, SourceClassifier value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("packageName");
        IrMessagePack.WriteString(ref writer, value.PackageName);
        writer.Write("ownerPath");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.OwnerPath.Length);
        foreach (var item in value.OwnerPath)
        {
            IrMessagePack.WriteString(ref writer, item);
        }
        writer.Write("name");
        IrMessagePack.WriteString(ref writer, value.Name);
    }

    private static SourceTypeParameter ReadSourceTypeParameter(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        int id = default;
        string? name = default;
        string? variance = default;
        SourceType[]? bounds = default;
        SourceAnnotation[]? annotations = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "id":
                    Seen(ref seen, 1UL);
                    id = reader.ReadInt32();
                    break;
                case "name":
                    Seen(ref seen, 2UL);
                    name = ReadText(ref reader);
                    break;
                case "variance":
                    Seen(ref seen, 4UL);
                    variance = ReadText(ref reader);
                    break;
                case "bounds":
                    Seen(ref seen, 8UL);
                    bounds = ReadArray(ref reader, ReadSourceType);
                    break;
                case "annotations":
                    Seen(ref seen, 16UL);
                    annotations = ReadArray(ref reader, ReadSourceAnnotation);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 31UL) != 31UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeParameter(id, name!, variance!, bounds!, annotations!);
    }

    private static void WriteSourceTypeParameter(ref MessagePackWriter writer, SourceTypeParameter value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(5);
        writer.Write("id");
        writer.Write(value.Id);
        writer.Write("name");
        IrMessagePack.WriteString(ref writer, value.Name);
        writer.Write("variance");
        IrMessagePack.WriteString(ref writer, value.Variance);
        writer.Write("bounds");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Bounds.Length);
        foreach (var item in value.Bounds)
        {
            WriteSourceType(ref writer, item, depth + 2);
        }
        writer.Write("annotations");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Annotations.Length);
        foreach (var item in value.Annotations)
        {
            WriteSourceAnnotation(ref writer, item, depth + 2);
        }
    }

    private static SourceType ReadSourceType(ref MessagePackReader reader) =>
        Discriminator(reader, "kind") switch
        {
            "class" => ReadSourceTypeClassType(ref reader),
            "alias" => ReadSourceTypeAliasType(ref reader),
            "parameter" => ReadSourceTypeParameterType(ref reader),
            _ => throw new FormatException(),
        };

    private static void WriteSourceType(ref MessagePackWriter writer, SourceType value, int depth)
    {
        switch (value)
        {
            case SourceType.ClassType node: WriteSourceTypeClassType(ref writer, node, depth); break;
            case SourceType.AliasType node: WriteSourceTypeAliasType(ref writer, node, depth); break;
            case SourceType.ParameterType node: WriteSourceTypeParameterType(ref writer, node, depth); break;
            default: throw new FormatException();
        }
    }

    private static SourceType.ClassType ReadSourceTypeClassType(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceClassifier? classifier = default;
        bool nullable = default;
        bool suspend = default;
        bool definitelyNotNull = default;
        SourceTypeArgument[]? arguments = default;
        SourceAnnotation[]? annotations = default;
        SourceType? outerType = default;
        SourceType? abbreviatedType = default;
        SourceType? flexibleUpperBound = default;
        string? flexibleCapabilities = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "kind":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "class") throw new FormatException();
                    break;
                case "classifier":
                    Seen(ref seen, 2UL);
                    classifier = ReadSourceClassifier(ref reader);
                    break;
                case "nullable":
                    Seen(ref seen, 4UL);
                    nullable = reader.ReadBoolean();
                    break;
                case "suspend":
                    Seen(ref seen, 8UL);
                    suspend = reader.ReadBoolean();
                    break;
                case "definitelyNotNull":
                    Seen(ref seen, 16UL);
                    definitelyNotNull = reader.ReadBoolean();
                    break;
                case "arguments":
                    Seen(ref seen, 32UL);
                    arguments = ReadArray(ref reader, ReadSourceTypeArgument);
                    break;
                case "annotations":
                    Seen(ref seen, 64UL);
                    annotations = ReadArray(ref reader, ReadSourceAnnotation);
                    break;
                case "outerType":
                    Seen(ref seen, 128UL);
                    outerType = ReadSourceType(ref reader);
                    break;
                case "abbreviatedType":
                    Seen(ref seen, 256UL);
                    abbreviatedType = ReadSourceType(ref reader);
                    break;
                case "flexibleUpperBound":
                    Seen(ref seen, 512UL);
                    flexibleUpperBound = ReadSourceType(ref reader);
                    break;
                case "flexibleCapabilities":
                    Seen(ref seen, 1024UL);
                    flexibleCapabilities = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 127UL) != 127UL) throw new FormatException();
        reader.Depth--;
        return new SourceType.ClassType(classifier!, nullable, suspend, definitelyNotNull, arguments!, annotations!, outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities);
    }

    private static void WriteSourceTypeClassType(ref MessagePackWriter writer, SourceType.ClassType value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(7 + (value.OuterType is null ? 0 : 1) + (value.AbbreviatedType is null ? 0 : 1) + (value.FlexibleUpperBound is null ? 0 : 1) + (value.FlexibleCapabilities is null ? 0 : 1));
        writer.Write("kind");
        writer.Write("class");
        writer.Write("classifier");
        WriteSourceClassifier(ref writer, value.Classifier, depth + 1);
        writer.Write("nullable");
        writer.Write(value.Nullable);
        writer.Write("suspend");
        writer.Write(value.Suspend);
        writer.Write("definitelyNotNull");
        writer.Write(value.DefinitelyNotNull);
        writer.Write("arguments");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Arguments.Length);
        foreach (var item in value.Arguments)
        {
            WriteSourceTypeArgument(ref writer, item, depth + 2);
        }
        writer.Write("annotations");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Annotations.Length);
        foreach (var item in value.Annotations)
        {
            WriteSourceAnnotation(ref writer, item, depth + 2);
        }
        if (value.OuterType is { } outerType)
        {
            writer.Write("outerType");
            WriteSourceType(ref writer, outerType, depth + 1);
        }
        if (value.AbbreviatedType is { } abbreviatedType)
        {
            writer.Write("abbreviatedType");
            WriteSourceType(ref writer, abbreviatedType, depth + 1);
        }
        if (value.FlexibleUpperBound is { } flexibleUpperBound)
        {
            writer.Write("flexibleUpperBound");
            WriteSourceType(ref writer, flexibleUpperBound, depth + 1);
        }
        if (value.FlexibleCapabilities is { } flexibleCapabilities)
        {
            writer.Write("flexibleCapabilities");
            IrMessagePack.WriteString(ref writer, flexibleCapabilities);
        }
    }

    private static SourceType.AliasType ReadSourceTypeAliasType(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceClassifier? classifier = default;
        bool nullable = default;
        bool suspend = default;
        bool definitelyNotNull = default;
        SourceTypeArgument[]? arguments = default;
        SourceAnnotation[]? annotations = default;
        SourceType? outerType = default;
        SourceType? abbreviatedType = default;
        SourceType? flexibleUpperBound = default;
        string? flexibleCapabilities = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "kind":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "alias") throw new FormatException();
                    break;
                case "classifier":
                    Seen(ref seen, 2UL);
                    classifier = ReadSourceClassifier(ref reader);
                    break;
                case "nullable":
                    Seen(ref seen, 4UL);
                    nullable = reader.ReadBoolean();
                    break;
                case "suspend":
                    Seen(ref seen, 8UL);
                    suspend = reader.ReadBoolean();
                    break;
                case "definitelyNotNull":
                    Seen(ref seen, 16UL);
                    definitelyNotNull = reader.ReadBoolean();
                    break;
                case "arguments":
                    Seen(ref seen, 32UL);
                    arguments = ReadArray(ref reader, ReadSourceTypeArgument);
                    break;
                case "annotations":
                    Seen(ref seen, 64UL);
                    annotations = ReadArray(ref reader, ReadSourceAnnotation);
                    break;
                case "outerType":
                    Seen(ref seen, 128UL);
                    outerType = ReadSourceType(ref reader);
                    break;
                case "abbreviatedType":
                    Seen(ref seen, 256UL);
                    abbreviatedType = ReadSourceType(ref reader);
                    break;
                case "flexibleUpperBound":
                    Seen(ref seen, 512UL);
                    flexibleUpperBound = ReadSourceType(ref reader);
                    break;
                case "flexibleCapabilities":
                    Seen(ref seen, 1024UL);
                    flexibleCapabilities = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 127UL) != 127UL) throw new FormatException();
        reader.Depth--;
        return new SourceType.AliasType(classifier!, nullable, suspend, definitelyNotNull, arguments!, annotations!, outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities);
    }

    private static void WriteSourceTypeAliasType(ref MessagePackWriter writer, SourceType.AliasType value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(7 + (value.OuterType is null ? 0 : 1) + (value.AbbreviatedType is null ? 0 : 1) + (value.FlexibleUpperBound is null ? 0 : 1) + (value.FlexibleCapabilities is null ? 0 : 1));
        writer.Write("kind");
        writer.Write("alias");
        writer.Write("classifier");
        WriteSourceClassifier(ref writer, value.Classifier, depth + 1);
        writer.Write("nullable");
        writer.Write(value.Nullable);
        writer.Write("suspend");
        writer.Write(value.Suspend);
        writer.Write("definitelyNotNull");
        writer.Write(value.DefinitelyNotNull);
        writer.Write("arguments");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Arguments.Length);
        foreach (var item in value.Arguments)
        {
            WriteSourceTypeArgument(ref writer, item, depth + 2);
        }
        writer.Write("annotations");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Annotations.Length);
        foreach (var item in value.Annotations)
        {
            WriteSourceAnnotation(ref writer, item, depth + 2);
        }
        if (value.OuterType is { } outerType)
        {
            writer.Write("outerType");
            WriteSourceType(ref writer, outerType, depth + 1);
        }
        if (value.AbbreviatedType is { } abbreviatedType)
        {
            writer.Write("abbreviatedType");
            WriteSourceType(ref writer, abbreviatedType, depth + 1);
        }
        if (value.FlexibleUpperBound is { } flexibleUpperBound)
        {
            writer.Write("flexibleUpperBound");
            WriteSourceType(ref writer, flexibleUpperBound, depth + 1);
        }
        if (value.FlexibleCapabilities is { } flexibleCapabilities)
        {
            writer.Write("flexibleCapabilities");
            IrMessagePack.WriteString(ref writer, flexibleCapabilities);
        }
    }

    private static SourceType.ParameterType ReadSourceTypeParameterType(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        int id = default;
        bool nullable = default;
        bool suspend = default;
        bool definitelyNotNull = default;
        SourceTypeArgument[]? arguments = default;
        SourceAnnotation[]? annotations = default;
        SourceType? outerType = default;
        SourceType? abbreviatedType = default;
        SourceType? flexibleUpperBound = default;
        string? flexibleCapabilities = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "kind":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "parameter") throw new FormatException();
                    break;
                case "id":
                    Seen(ref seen, 2UL);
                    id = reader.ReadInt32();
                    break;
                case "nullable":
                    Seen(ref seen, 4UL);
                    nullable = reader.ReadBoolean();
                    break;
                case "suspend":
                    Seen(ref seen, 8UL);
                    suspend = reader.ReadBoolean();
                    break;
                case "definitelyNotNull":
                    Seen(ref seen, 16UL);
                    definitelyNotNull = reader.ReadBoolean();
                    break;
                case "arguments":
                    Seen(ref seen, 32UL);
                    arguments = ReadArray(ref reader, ReadSourceTypeArgument);
                    break;
                case "annotations":
                    Seen(ref seen, 64UL);
                    annotations = ReadArray(ref reader, ReadSourceAnnotation);
                    break;
                case "outerType":
                    Seen(ref seen, 128UL);
                    outerType = ReadSourceType(ref reader);
                    break;
                case "abbreviatedType":
                    Seen(ref seen, 256UL);
                    abbreviatedType = ReadSourceType(ref reader);
                    break;
                case "flexibleUpperBound":
                    Seen(ref seen, 512UL);
                    flexibleUpperBound = ReadSourceType(ref reader);
                    break;
                case "flexibleCapabilities":
                    Seen(ref seen, 1024UL);
                    flexibleCapabilities = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 127UL) != 127UL) throw new FormatException();
        reader.Depth--;
        return new SourceType.ParameterType(id, nullable, suspend, definitelyNotNull, arguments!, annotations!, outerType, abbreviatedType, flexibleUpperBound, flexibleCapabilities);
    }

    private static void WriteSourceTypeParameterType(ref MessagePackWriter writer, SourceType.ParameterType value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(7 + (value.OuterType is null ? 0 : 1) + (value.AbbreviatedType is null ? 0 : 1) + (value.FlexibleUpperBound is null ? 0 : 1) + (value.FlexibleCapabilities is null ? 0 : 1));
        writer.Write("kind");
        writer.Write("parameter");
        writer.Write("id");
        writer.Write(value.Id);
        writer.Write("nullable");
        writer.Write(value.Nullable);
        writer.Write("suspend");
        writer.Write(value.Suspend);
        writer.Write("definitelyNotNull");
        writer.Write(value.DefinitelyNotNull);
        writer.Write("arguments");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Arguments.Length);
        foreach (var item in value.Arguments)
        {
            WriteSourceTypeArgument(ref writer, item, depth + 2);
        }
        writer.Write("annotations");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Annotations.Length);
        foreach (var item in value.Annotations)
        {
            WriteSourceAnnotation(ref writer, item, depth + 2);
        }
        if (value.OuterType is { } outerType)
        {
            writer.Write("outerType");
            WriteSourceType(ref writer, outerType, depth + 1);
        }
        if (value.AbbreviatedType is { } abbreviatedType)
        {
            writer.Write("abbreviatedType");
            WriteSourceType(ref writer, abbreviatedType, depth + 1);
        }
        if (value.FlexibleUpperBound is { } flexibleUpperBound)
        {
            writer.Write("flexibleUpperBound");
            WriteSourceType(ref writer, flexibleUpperBound, depth + 1);
        }
        if (value.FlexibleCapabilities is { } flexibleCapabilities)
        {
            writer.Write("flexibleCapabilities");
            IrMessagePack.WriteString(ref writer, flexibleCapabilities);
        }
    }

    private static SourceTypeArgument ReadSourceTypeArgument(ref MessagePackReader reader) =>
        Discriminator(reader, "variance") switch
        {
            "inv" => ReadSourceTypeArgumentInvArgument(ref reader),
            "in" => ReadSourceTypeArgumentInArgument(ref reader),
            "out" => ReadSourceTypeArgumentOutArgument(ref reader),
            "star" => ReadSourceTypeArgumentStarArgument(ref reader),
            _ => throw new FormatException(),
        };

    private static void WriteSourceTypeArgument(ref MessagePackWriter writer, SourceTypeArgument value, int depth)
    {
        switch (value)
        {
            case SourceTypeArgument.InvArgument node: WriteSourceTypeArgumentInvArgument(ref writer, node, depth); break;
            case SourceTypeArgument.InArgument node: WriteSourceTypeArgumentInArgument(ref writer, node, depth); break;
            case SourceTypeArgument.OutArgument node: WriteSourceTypeArgumentOutArgument(ref writer, node, depth); break;
            case SourceTypeArgument.StarArgument node: WriteSourceTypeArgumentStarArgument(ref writer, node, depth); break;
            default: throw new FormatException();
        }
    }

    private static SourceTypeArgument.InvArgument ReadSourceTypeArgumentInvArgument(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceType? type = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "variance":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "inv") throw new FormatException();
                    break;
                case "type":
                    Seen(ref seen, 2UL);
                    type = ReadSourceType(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 3UL) != 3UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeArgument.InvArgument(type!);
    }

    private static void WriteSourceTypeArgumentInvArgument(ref MessagePackWriter writer, SourceTypeArgument.InvArgument value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(2);
        writer.Write("variance");
        writer.Write("inv");
        writer.Write("type");
        WriteSourceType(ref writer, value.Type, depth + 1);
    }

    private static SourceTypeArgument.InArgument ReadSourceTypeArgumentInArgument(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceType? type = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "variance":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "in") throw new FormatException();
                    break;
                case "type":
                    Seen(ref seen, 2UL);
                    type = ReadSourceType(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 3UL) != 3UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeArgument.InArgument(type!);
    }

    private static void WriteSourceTypeArgumentInArgument(ref MessagePackWriter writer, SourceTypeArgument.InArgument value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(2);
        writer.Write("variance");
        writer.Write("in");
        writer.Write("type");
        WriteSourceType(ref writer, value.Type, depth + 1);
    }

    private static SourceTypeArgument.OutArgument ReadSourceTypeArgumentOutArgument(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceType? type = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "variance":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "out") throw new FormatException();
                    break;
                case "type":
                    Seen(ref seen, 2UL);
                    type = ReadSourceType(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 3UL) != 3UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeArgument.OutArgument(type!);
    }

    private static void WriteSourceTypeArgumentOutArgument(ref MessagePackWriter writer, SourceTypeArgument.OutArgument value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(2);
        writer.Write("variance");
        writer.Write("out");
        writer.Write("type");
        WriteSourceType(ref writer, value.Type, depth + 1);
    }

    private static SourceTypeArgument.StarArgument ReadSourceTypeArgumentStarArgument(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "variance":
                    Seen(ref seen, 1UL);
                    if (ReadText(ref reader) != "star") throw new FormatException();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 1UL) != 1UL) throw new FormatException();
        reader.Depth--;
        return new SourceTypeArgument.StarArgument();
    }

    private static void WriteSourceTypeArgumentStarArgument(ref MessagePackWriter writer, SourceTypeArgument.StarArgument value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(1);
        writer.Write("variance");
        writer.Write("star");
    }

    private static SourceAnnotation ReadSourceAnnotation(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        SourceClassifier? classifier = default;
        SourceAnnotationArgument[]? arguments = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "classifier":
                    Seen(ref seen, 1UL);
                    classifier = ReadSourceClassifier(ref reader);
                    break;
                case "arguments":
                    Seen(ref seen, 2UL);
                    arguments = ReadArray(ref reader, ReadSourceAnnotationArgument);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 3UL) != 3UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotation(classifier!, arguments!);
    }

    private static void WriteSourceAnnotation(ref MessagePackWriter writer, SourceAnnotation value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(2);
        writer.Write("classifier");
        WriteSourceClassifier(ref writer, value.Classifier, depth + 1);
        writer.Write("arguments");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Arguments.Length);
        foreach (var item in value.Arguments)
        {
            WriteSourceAnnotationArgument(ref writer, item, depth + 2);
        }
    }

    private static SourceAnnotationArgument ReadSourceAnnotationArgument(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        string? name = default;
        SourceAnnotationValue? value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "name":
                    Seen(ref seen, 1UL);
                    name = ReadText(ref reader);
                    break;
                case "value":
                    Seen(ref seen, 2UL);
                    value = ReadSourceAnnotationValue(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 3UL) != 3UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationArgument(name!, value!);
    }

    private static void WriteSourceAnnotationArgument(ref MessagePackWriter writer, SourceAnnotationArgument value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(2);
        writer.Write("name");
        IrMessagePack.WriteString(ref writer, value.Name);
        writer.Write("value");
        WriteSourceAnnotationValue(ref writer, value.Value, depth + 1);
    }

    private static SourceAnnotationValue ReadSourceAnnotationValue(ref MessagePackReader reader) =>
        Discriminator(reader, "kind") switch
        {
            "byte" => ReadSourceAnnotationValueByteValue(ref reader),
            "char" => ReadSourceAnnotationValueCharValue(ref reader),
            "short" => ReadSourceAnnotationValueShortValue(ref reader),
            "int" => ReadSourceAnnotationValueIntValue(ref reader),
            "long" => ReadSourceAnnotationValueLongValue(ref reader),
            "boolean" => ReadSourceAnnotationValueBooleanValue(ref reader),
            "string" => ReadSourceAnnotationValueStringValue(ref reader),
            "float" => ReadSourceAnnotationValueFloatValue(ref reader),
            "double" => ReadSourceAnnotationValueDoubleValue(ref reader),
            "class" => ReadSourceAnnotationValueClassValue(ref reader),
            "enum" => ReadSourceAnnotationValueEnumValue(ref reader),
            "annotation" => ReadSourceAnnotationValueAnnotationValue(ref reader),
            "array" => ReadSourceAnnotationValueArrayValue(ref reader),
            _ => throw new FormatException(),
        };

    private static void WriteSourceAnnotationValue(ref MessagePackWriter writer, SourceAnnotationValue value, int depth)
    {
        switch (value)
        {
            case SourceAnnotationValue.ByteValue node: WriteSourceAnnotationValueByteValue(ref writer, node, depth); break;
            case SourceAnnotationValue.CharValue node: WriteSourceAnnotationValueCharValue(ref writer, node, depth); break;
            case SourceAnnotationValue.ShortValue node: WriteSourceAnnotationValueShortValue(ref writer, node, depth); break;
            case SourceAnnotationValue.IntValue node: WriteSourceAnnotationValueIntValue(ref writer, node, depth); break;
            case SourceAnnotationValue.LongValue node: WriteSourceAnnotationValueLongValue(ref writer, node, depth); break;
            case SourceAnnotationValue.BooleanValue node: WriteSourceAnnotationValueBooleanValue(ref writer, node, depth); break;
            case SourceAnnotationValue.StringValue node: WriteSourceAnnotationValueStringValue(ref writer, node, depth); break;
            case SourceAnnotationValue.FloatValue node: WriteSourceAnnotationValueFloatValue(ref writer, node, depth); break;
            case SourceAnnotationValue.DoubleValue node: WriteSourceAnnotationValueDoubleValue(ref writer, node, depth); break;
            case SourceAnnotationValue.ClassValue node: WriteSourceAnnotationValueClassValue(ref writer, node, depth); break;
            case SourceAnnotationValue.EnumValue node: WriteSourceAnnotationValueEnumValue(ref writer, node, depth); break;
            case SourceAnnotationValue.AnnotationValue node: WriteSourceAnnotationValueAnnotationValue(ref writer, node, depth); break;
            case SourceAnnotationValue.ArrayValue node: WriteSourceAnnotationValueArrayValue(ref writer, node, depth); break;
            default: throw new FormatException();
        }
    }

    private static SourceAnnotationValue.ByteValue ReadSourceAnnotationValueByteValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "byte") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.ByteValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueByteValue(ref MessagePackWriter writer, SourceAnnotationValue.ByteValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("byte");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.CharValue ReadSourceAnnotationValueCharValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "char") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.CharValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueCharValue(ref MessagePackWriter writer, SourceAnnotationValue.CharValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("char");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.ShortValue ReadSourceAnnotationValueShortValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "short") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.ShortValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueShortValue(ref MessagePackWriter writer, SourceAnnotationValue.ShortValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("short");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.IntValue ReadSourceAnnotationValueIntValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "int") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.IntValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueIntValue(ref MessagePackWriter writer, SourceAnnotationValue.IntValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("int");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.LongValue ReadSourceAnnotationValueLongValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "long") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.LongValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueLongValue(ref MessagePackWriter writer, SourceAnnotationValue.LongValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("long");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.BooleanValue ReadSourceAnnotationValueBooleanValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "boolean") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.BooleanValue(unsigned, value);
    }

    private static void WriteSourceAnnotationValueBooleanValue(ref MessagePackWriter writer, SourceAnnotationValue.BooleanValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("boolean");
        writer.Write("value");
        writer.Write(value.Value);
    }

    private static SourceAnnotationValue.StringValue ReadSourceAnnotationValueStringValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        string? value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "string") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.StringValue(unsigned, value!);
    }

    private static void WriteSourceAnnotationValueStringValue(ref MessagePackWriter writer, SourceAnnotationValue.StringValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("string");
        writer.Write("value");
        IrMessagePack.WriteString(ref writer, value.Value);
    }

    private static SourceAnnotationValue.FloatValue ReadSourceAnnotationValueFloatValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long bits = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "float") throw new FormatException();
                    break;
                case "bits":
                    Seen(ref seen, 4UL);
                    bits = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.FloatValue(unsigned, bits);
    }

    private static void WriteSourceAnnotationValueFloatValue(ref MessagePackWriter writer, SourceAnnotationValue.FloatValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("float");
        writer.Write("bits");
        writer.Write(value.Bits);
    }

    private static SourceAnnotationValue.DoubleValue ReadSourceAnnotationValueDoubleValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        long bits = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "double") throw new FormatException();
                    break;
                case "bits":
                    Seen(ref seen, 4UL);
                    bits = reader.ReadInt64();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.DoubleValue(unsigned, bits);
    }

    private static void WriteSourceAnnotationValueDoubleValue(ref MessagePackWriter writer, SourceAnnotationValue.DoubleValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("double");
        writer.Write("bits");
        writer.Write(value.Bits);
    }

    private static SourceAnnotationValue.ClassValue ReadSourceAnnotationValueClassValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        SourceClassifier? classifier = default;
        int arrayDimensions = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "class") throw new FormatException();
                    break;
                case "classifier":
                    Seen(ref seen, 4UL);
                    classifier = ReadSourceClassifier(ref reader);
                    break;
                case "arrayDimensions":
                    Seen(ref seen, 8UL);
                    arrayDimensions = reader.ReadInt32();
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 15UL) != 15UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.ClassValue(unsigned, classifier!, arrayDimensions);
    }

    private static void WriteSourceAnnotationValueClassValue(ref MessagePackWriter writer, SourceAnnotationValue.ClassValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(4);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("class");
        writer.Write("classifier");
        WriteSourceClassifier(ref writer, value.Classifier, depth + 1);
        writer.Write("arrayDimensions");
        writer.Write(value.ArrayDimensions);
    }

    private static SourceAnnotationValue.EnumValue ReadSourceAnnotationValueEnumValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        SourceClassifier? classifier = default;
        string? name = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "enum") throw new FormatException();
                    break;
                case "classifier":
                    Seen(ref seen, 4UL);
                    classifier = ReadSourceClassifier(ref reader);
                    break;
                case "name":
                    Seen(ref seen, 8UL);
                    name = ReadText(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 15UL) != 15UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.EnumValue(unsigned, classifier!, name!);
    }

    private static void WriteSourceAnnotationValueEnumValue(ref MessagePackWriter writer, SourceAnnotationValue.EnumValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(4);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("enum");
        writer.Write("classifier");
        WriteSourceClassifier(ref writer, value.Classifier, depth + 1);
        writer.Write("name");
        IrMessagePack.WriteString(ref writer, value.Name);
    }

    private static SourceAnnotationValue.AnnotationValue ReadSourceAnnotationValueAnnotationValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        SourceAnnotation? value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "annotation") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = ReadSourceAnnotation(ref reader);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.AnnotationValue(unsigned, value!);
    }

    private static void WriteSourceAnnotationValueAnnotationValue(ref MessagePackWriter writer, SourceAnnotationValue.AnnotationValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("annotation");
        writer.Write("value");
        WriteSourceAnnotation(ref writer, value.Value, depth + 1);
    }

    private static SourceAnnotationValue.ArrayValue ReadSourceAnnotationValueArrayValue(ref MessagePackReader reader)
    {
        var count = ReadMap(ref reader);
        ulong seen = 0;
        bool unsigned = default;
        SourceAnnotationValue[]? value = default;
        for (var i = 0; i < count; i++)
        {
            switch (ReadText(ref reader))
            {
                case "unsigned":
                    Seen(ref seen, 1UL);
                    unsigned = reader.ReadBoolean();
                    break;
                case "kind":
                    Seen(ref seen, 2UL);
                    if (ReadText(ref reader) != "array") throw new FormatException();
                    break;
                case "value":
                    Seen(ref seen, 4UL);
                    value = ReadArray(ref reader, ReadSourceAnnotationValue);
                    break;
                default: throw new FormatException();
            }
        }
        if ((seen & 7UL) != 7UL) throw new FormatException();
        reader.Depth--;
        return new SourceAnnotationValue.ArrayValue(unsigned, value!);
    }

    private static void WriteSourceAnnotationValueArrayValue(ref MessagePackWriter writer, SourceAnnotationValue.ArrayValue value, int depth)
    {
        if (depth >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteMapHeader(3);
        writer.Write("unsigned");
        writer.Write(value.Unsigned);
        writer.Write("kind");
        writer.Write("array");
        writer.Write("value");
        if (depth + 1 >= BirJson.MaxDepth) throw new FormatException();
        writer.WriteArrayHeader(value.Value.Length);
        foreach (var item in value.Value)
        {
            WriteSourceAnnotationValue(ref writer, item, depth + 2);
        }
    }

    private delegate T Reader<T>(ref MessagePackReader reader);
    private static T[] ReadArray<T>(ref MessagePackReader reader, Reader<T> read)
    {
        Enter(ref reader);
        var count = reader.ReadArrayHeader();
        var values = new T[count];
        for (var i = 0; i < count; i++) values[i] = read(ref reader);
        reader.Depth--;
        return values;
    }
    private static int ReadMap(ref MessagePackReader reader)
    {
        Enter(ref reader);
        return reader.ReadMapHeader();
    }
    private static void Enter(ref MessagePackReader reader)
    {
        if (reader.Depth++ >= BirJson.MaxDepth) throw new FormatException();
    }
    private static string Discriminator(MessagePackReader reader, string property)
    {
        var count = ReadMap(ref reader);
        string? result = null;
        for (var i = 0; i < count; i++)
        {
            if (ReadText(ref reader) == property)
            {
                if (result != null) throw new FormatException();
                result = ReadText(ref reader);
            }
            else reader.Skip();
        }
        return result ?? throw new FormatException();
    }
    private static void Seen(ref ulong seen, ulong bit)
    {
        if ((seen & bit) != 0) throw new FormatException();
        seen |= bit;
    }
    private static string ReadText(ref MessagePackReader reader) => IrMessagePack.ReadString(ref reader);
}
