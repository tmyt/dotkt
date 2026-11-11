#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DotKt.Bir;

namespace DotKt.Cir;

// The scalar records are shared by metadata constants and executable constants.
// Only the schema-declared scalar subset implements this role.
[JsonConverter(typeof(ScalarValueJsonConverter))]
public interface IScalarValue;

public sealed class ScalarValueJsonConverter : JsonConverter<IScalarValue>
{
    public override IScalarValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<AttributeArgument>(ref reader, options) as IScalarValue ?? throw new JsonException();
    public override void Write(Utf8JsonWriter writer, IScalarValue value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (AttributeArgument)value, options);
}

// JSON is a boundary representation. No typed metadata value contains a DOM or arbitrary field bag.
public static class AttributeJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = BirJson.MaxDepth,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new TypeNodeMetadataConverter());
        options.Converters.Add(new CirChildrenArrayJsonConverterFactory());
        return options;
    }

    public static AttributeApplication Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        TypeNode? attr = null;
        TypeNode[]? argTypes = null;
        AttributeArgument[]? args = null;
        bool? external = null;
        string? assembly = null;
        MemberRefNode? member = null;
        NamedAttributeArgument[]? named = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var key = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            switch (key)
            {
                case "attr": attr = TypeNode.ReadJson(ref reader); break;
                case "argTypes": argTypes = JsonSerializer.Deserialize<TypeNode[]>(ref reader, Options); break;
                case "args": args = JsonSerializer.Deserialize<AttributeArgument[]>(ref reader, Options); break;
                case "attrExternal": external = reader.GetBoolean(); break;
                case "attrAssembly": assembly = reader.GetString(); break;
                case "memberRef": member = JsonSerializer.Deserialize<MemberRefNode>(ref reader, Options); break;
                case "namedArgs": named = JsonSerializer.Deserialize<NamedAttributeArgument[]>(ref reader, Options); break;
                default: throw new JsonException();
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
        member?.Validate();
        return new AttributeApplication(attr ?? throw new JsonException(), argTypes ?? throw new JsonException(),
            args ?? throw new JsonException(), external, assembly, member, named);
    }

    public static void Write(Utf8JsonWriter writer, AttributeApplication attribute)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("attr");
        TypeNode.WriteJson(writer, attribute.Attr);
        writer.WritePropertyName("argTypes");
        JsonSerializer.Serialize(writer, attribute.ArgTypes, Options);
        writer.WritePropertyName("args");
        JsonSerializer.Serialize(writer, attribute.Args, Options);
        if (attribute.AttrExternal is bool external) writer.WriteBoolean("attrExternal", external);
        if (attribute.AttrAssembly is string assembly) writer.WriteString("attrAssembly", assembly);
        if (attribute.MemberRef is MemberRefNode member)
        {
            writer.WritePropertyName("memberRef");
            JsonSerializer.Serialize(writer, member, Options);
        }
        if (attribute.NamedArgs is { } named)
        {
            writer.WritePropertyName("namedArgs");
            JsonSerializer.Serialize(writer, named, Options);
        }
        writer.WriteEndObject();
    }
}

public sealed class AttributeApplicationJsonConverter : JsonConverter<AttributeApplication>
{
    public override AttributeApplication Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => AttributeJson.Read(ref reader);
    public override void Write(Utf8JsonWriter writer, AttributeApplication value, JsonSerializerOptions options)
        => AttributeJson.Write(writer, value);
}

public sealed class TypeNodeMetadataConverter : JsonConverter<TypeNode>
{
    public override TypeNode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => TypeNode.ReadJson(ref reader);
    public override void Write(Utf8JsonWriter writer, TypeNode value, JsonSerializerOptions options)
        => TypeNode.WriteJson(writer, value);
}
