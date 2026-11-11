#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotKt.Cir;

public interface ICirChildren
{
    IEnumerable<ICirNode> Children { get; }
}

public interface ICirNode : ICirChildren
{
    string Tag { get; }
}

[JsonConverter(typeof(CirStatementJsonConverter))]
public interface ICirStatement : ICirNode;

[JsonConverter(typeof(CirExpressionJsonConverter))]
public interface ICirExpression : ICirNode;

// Discrimination is by the closed node tag. The declared slot then enforces its
// expression/statement role; no field name or arbitrary object shape selects it.
public sealed class CirStatementJsonConverter : JsonConverter<ICirStatement>
{
    public override bool HandleNull => true;
    public override ICirStatement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<CirNode>(ref reader, options) as ICirStatement ?? throw new JsonException();
    public override void Write(Utf8JsonWriter writer, ICirStatement value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (CirNode)value, options);
}

public sealed class CirExpressionJsonConverter : JsonConverter<ICirExpression>
{
    public override bool HandleNull => true;
    public override ICirExpression Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<CirNode>(ref reader, options) as ICirExpression ?? throw new JsonException();
    public override void Write(Utf8JsonWriter writer, ICirExpression value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (CirNode)value, options);
}

// Executable child slots never contain null elements. NullableAnnotations does
// not enforce collection elements, so enforce that same schema rule here.
public sealed class CirChildrenArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsArray
        && typeof(ICirChildren).IsAssignableFrom(type.GetElementType());
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(CirChildrenArrayJsonConverter<>).MakeGenericType(type.GetElementType()!))!;
}

public sealed class CirChildrenArrayJsonConverter<T> : JsonConverter<T[]> where T : ICirChildren
{
    public override T[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
        var items = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var item = JsonSerializer.Deserialize<T>(ref reader, options);
            if (item is null) throw new JsonException();
            items.Add(item);
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
        return items.ToArray();
    }
    public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is null) throw new JsonException();
            JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }
}

public static class CirExecutableWalk
{
    public static IEnumerable<ICirNode> DescendantsAndSelf(ICirNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }
}
