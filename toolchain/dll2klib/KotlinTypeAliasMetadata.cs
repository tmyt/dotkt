using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json.Nodes;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;

// This is declaration projection, not CLR signature decoding. Classifier paths and alias-owned parameter IDs
// come directly from the actualized frontend metadata; no TypeDef, facade, or physical generic frame is inferred.
internal sealed class KotlinTypeAliasMetadata
{
    internal const string AttributeName = MetadataAttributes.DotKtNs + "KotlinTypeAliasAttribute";
    readonly IReadOnlyList<JsonObject> _declarations;

    public KotlinTypeAliasMetadata(MetadataReader reader, MetadataAttributes attributes)
    {
        attributes.ValidateCarrierTargets(AttributeName, HandleKind.AssemblyDefinition);
        _declarations = (reader.IsAssembly
            ? attributes.CarrierBodies(MetadataTokens.EntityHandle(0x20000001), AttributeName)
            : Array.Empty<JsonNode>())
            .Select(node => node as JsonObject ?? throw new InvalidDataException("typealias declaration must be an object"))
            .ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in _declarations)
            if (!identities.Add(Identity(declaration)))
                throw new InvalidDataException($"duplicate Kotlin typealias declaration '{Identity(declaration)}'");
    }

    public IEnumerable<string> Packages => _declarations.Select(declaration => Text(declaration, "packageName"));

    public void Project(string packageName, PackageFragment fragment, NameTable names)
    {
        var owners = fragment.Class.ToDictionary(klass => names.ClassName(klass.FqName)!, StringComparer.Ordinal);
        foreach (var declaration in _declarations.Where(declaration => Text(declaration, "packageName") == packageName))
        {
            var ownerPath = Strings(declaration, "ownerPath");
            var alias = new TypeAlias
            {
                Name = names.String(Text(declaration, "name")),
                Flags = Visibility(Text(declaration, "visibility")),
                UnderlyingType = Type(Object(declaration, "underlyingType"), names),
                ExpandedType = Type(Object(declaration, "expandedType"), names),
            };
            foreach (var parameter in Objects(declaration, "typeParameters"))
            {
                var projected = new TypeParameter
                {
                    Id = Integer(parameter, "id"),
                    Name = names.String(Text(parameter, "name")),
                    Variance = Text(parameter, "variance") switch
                    {
                        "inv" => TypeParameter.Types.Variance.Inv,
                        "in" => TypeParameter.Types.Variance.In,
                        "out" => TypeParameter.Types.Variance.Out,
                        _ => throw new InvalidDataException("invalid typealias parameter variance"),
                    },
                };
                projected.UpperBound.Add(Objects(parameter, "bounds").Select(bound => Type(bound, names)));
                alias.TypeParameter.Add(projected);
            }
            alias.Annotation.Add(Objects(declaration, "annotations").Select(annotation => ReadAnnotation(annotation, names)));
            if (alias.Annotation.Count > 0) alias.Flags |= 1;
            if (ownerPath.Length == 0) fragment.Package.TypeAlias.Add(alias);
            else if (owners.TryGetValue(string.Join('.', ownerPath.Prepend(packageName).Where(part => part.Length > 0)), out var owner))
                owner.TypeAlias.Add(alias);
            // A source-invisible containing class is absent from the reference KLIB, including its aliases.
        }
    }

    static KType Type(JsonObject source, NameTable names)
    {
        var result = new KType
        {
            Nullable = Boolean(source, "nullable"),
            Flags = (Boolean(source, "suspend") ? 1 : 0) | (Boolean(source, "definitelyNotNull") ? 2 : 0),
        };
        switch (Text(source, "kind"))
        {
            case "class": result.ClassName = Classifier(Object(source, "classifier"), names); break;
            case "alias": result.TypeAliasName = Classifier(Object(source, "classifier"), names); break;
            case "parameter": result.TypeParameter = Integer(source, "id"); break;
            default: throw new InvalidDataException("invalid typealias type classifier kind");
        }
        foreach (var argument in Objects(source, "arguments"))
        {
            var projected = new KType.Types.Argument
            {
                Projection = Text(argument, "variance") switch
                {
                    "inv" => KType.Types.Argument.Types.Projection.Inv,
                    "in" => KType.Types.Argument.Types.Projection.In,
                    "out" => KType.Types.Argument.Types.Projection.Out,
                    "star" => KType.Types.Argument.Types.Projection.Star,
                    _ => throw new InvalidDataException("invalid typealias type-argument variance"),
                },
            };
            if (projected.Projection != KType.Types.Argument.Types.Projection.Star)
                projected.Type = Type(Object(argument, "type"), names);
            result.Argument.Add(projected);
        }
        result.TypeAnnotation.Add(Objects(source, "annotations").Select(annotation => ReadAnnotation(annotation, names)));
        if (source["outerType"] is JsonObject outer) result.OuterType = Type(outer, names);
        if (source["abbreviatedType"] is JsonObject abbreviated) result.AbbreviatedType = Type(abbreviated, names);
        if (source["flexibleUpperBound"] is JsonObject flexible)
        {
            result.FlexibleUpperBound = Type(flexible, names);
            result.FlexibleTypeCapabilitiesId = names.String(Text(source, "flexibleCapabilities"));
        }
        return result;
    }

    static Annotation ReadAnnotation(JsonObject source, NameTable names)
    {
        var result = new Annotation { Id = Classifier(Object(source, "classifier"), names) };
        foreach (var argument in Objects(source, "arguments"))
            result.Argument.Add(new Annotation.Types.Argument
            {
                NameId = names.String(Text(argument, "name")),
                Value = Value(Object(argument, "value"), names),
            });
        return result;
    }

    static Annotation.Types.Argument.Types.Value Value(JsonObject source, NameTable names)
    {
        var result = new Annotation.Types.Argument.Types.Value { Flags = Boolean(source, "unsigned") ? 1 : 0 };
        result.Type = Text(source, "kind") switch
        {
            "byte" => Annotation.Types.Argument.Types.Value.Types.Type.Byte,
            "char" => Annotation.Types.Argument.Types.Value.Types.Type.Char,
            "short" => Annotation.Types.Argument.Types.Value.Types.Type.Short,
            "int" => Annotation.Types.Argument.Types.Value.Types.Type.Int,
            "long" => Annotation.Types.Argument.Types.Value.Types.Type.Long,
            "float" => Annotation.Types.Argument.Types.Value.Types.Type.Float,
            "double" => Annotation.Types.Argument.Types.Value.Types.Type.Double,
            "boolean" => Annotation.Types.Argument.Types.Value.Types.Type.Boolean,
            "string" => Annotation.Types.Argument.Types.Value.Types.Type.String,
            "class" => Annotation.Types.Argument.Types.Value.Types.Type.Class,
            "enum" => Annotation.Types.Argument.Types.Value.Types.Type.Enum,
            "annotation" => Annotation.Types.Argument.Types.Value.Types.Type.Annotation,
            "array" => Annotation.Types.Argument.Types.Value.Types.Type.Array,
            _ => throw new InvalidDataException("invalid typealias annotation constant kind"),
        };
        switch (Text(source, "kind"))
        {
            case "byte": case "char": case "short": case "int": case "long": case "boolean":
                result.IntValue = source["value"]!.GetValue<long>(); break;
            case "float": result.FloatValue = BitConverter.Int32BitsToSingle(Integer(source, "bits")); break;
            case "double": result.DoubleValue = BitConverter.Int64BitsToDouble(source["bits"]!.GetValue<long>()); break;
            case "string": result.StringValue = names.String(Text(source, "value")); break;
            case "class":
                result.ClassId = Classifier(Object(source, "classifier"), names);
                result.ArrayDimensionCount = Integer(source, "arrayDimensions"); break;
            case "enum":
                result.ClassId = Classifier(Object(source, "classifier"), names);
                result.EnumValueId = names.String(Text(source, "name")); break;
            case "annotation": result.Annotation = ReadAnnotation(Object(source, "value"), names); break;
            case "array": result.ArrayElement.Add(Objects(source, "value").Select(value => Value(value, names))); break;
        }
        return result;
    }

    static int Classifier(JsonObject source, NameTable names) => names.Class(
        Text(source, "packageName"), Strings(source, "ownerPath").Append(Text(source, "name")));
    static string Identity(JsonObject source) => string.Join('.', Strings(source, "ownerPath")
        .Prepend(Text(source, "packageName")).Append(Text(source, "name")).Where(part => part.Length > 0));
    static int Visibility(string visibility) => visibility switch
    {
        "internal" => 0, "private" => 2, "protected" => 4, "public" => 6, "private_to_this" => 8, "local" => 10,
        _ => throw new InvalidDataException("invalid Kotlin typealias visibility"),
    };
    static string Text(JsonObject source, string key) => source[key]!.GetValue<string>();
    static int Integer(JsonObject source, string key) => source[key]!.GetValue<int>();
    static bool Boolean(JsonObject source, string key) => source[key]!.GetValue<bool>();
    static JsonObject Object(JsonObject source, string key) => source[key] as JsonObject
        ?? throw new InvalidDataException($"typealias '{key}' must be an object");
    static IEnumerable<JsonObject> Objects(JsonObject source, string key) => (source[key] as JsonArray
        ?? throw new InvalidDataException($"typealias '{key}' must be an array")).Select(node => node as JsonObject
            ?? throw new InvalidDataException($"typealias '{key}' entry must be an object"));
    static string[] Strings(JsonObject source, string key) => (source[key] as JsonArray
        ?? throw new InvalidDataException($"typealias '{key}' must be an array")).Select(node => node!.GetValue<string>()).ToArray();
}
