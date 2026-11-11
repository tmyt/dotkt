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
    public override ICirStatement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<CirNode>(ref reader, options) as ICirStatement ?? throw new JsonException();
    public override void Write(Utf8JsonWriter writer, ICirStatement value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (CirNode)value, options);
}

public sealed class CirExpressionJsonConverter : JsonConverter<ICirExpression>
{
    public override ICirExpression Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<CirNode>(ref reader, options) as ICirExpression ?? throw new JsonException();
    public override void Write(Utf8JsonWriter writer, ICirExpression value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (CirNode)value, options);
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
