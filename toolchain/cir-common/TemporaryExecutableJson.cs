#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotKt.Cir;

// Explicit executable-only migration seam. Declarations, metadata, signatures and arbitrary unknown fields
// cannot be stored here. Delete these adapters when typed CIR executable nodes replace EmitStmt/EmitExpr input.
// The producer writes its existing node directly at the file boundary; the consumer keeps a read-only subtree.
// Neither path stringifies and reparses a declaration to call an unmigrated consumer.
public abstract class TemporaryCirExecutable : IDisposable
{
    readonly JsonNode? _producer;
    readonly JsonDocument? _consumer;
    protected TemporaryCirExecutable(JsonNode? producer) { _producer = producer; }
    protected TemporaryCirExecutable(JsonDocument consumer) { _consumer = consumer; }
    public JsonElement Element => _consumer?.RootElement
        ?? throw new InvalidOperationException("Producer-backed executable is not an emitter input");
    internal void Write(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_consumer != null) _consumer.RootElement.WriteTo(writer);
        else if (_producer != null) _producer.WriteTo(writer, options);
        else writer.WriteNullValue();
    }
    public void Dispose() => _consumer?.Dispose();
}

[JsonConverter(typeof(TemporaryCirStatementsConverter))]
public sealed class TemporaryCirStatements : TemporaryCirExecutable
{
    TemporaryCirStatements(JsonNode? node) : base(node) { }
    internal TemporaryCirStatements(JsonDocument document) : base(document) { }
    public static TemporaryCirStatements FromNode(JsonNode node) => new(node ?? throw new JsonException());
}

[JsonConverter(typeof(TemporaryCirExpressionsConverter))]
public sealed class TemporaryCirExpressions : TemporaryCirExecutable
{
    TemporaryCirExpressions(JsonNode? node) : base(node) { }
    internal TemporaryCirExpressions(JsonDocument document) : base(document) { }
    public static TemporaryCirExpressions FromNode(JsonNode? node) => new(node);
}

[JsonConverter(typeof(TemporaryCirExpressionConverter))]
public sealed class TemporaryCirExpression : TemporaryCirExecutable
{
    TemporaryCirExpression(JsonNode? node) : base(node) { }
    internal TemporaryCirExpression(JsonDocument document) : base(document) { }
    public static TemporaryCirExpression FromNode(JsonNode? node) => new(node);
}

public sealed class TemporaryCirStatementsConverter : JsonConverter<TemporaryCirStatements>
{
    public override TemporaryCirStatements Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.StartArray ? new(JsonDocument.ParseValue(ref reader)) : throw new JsonException();
    public override void Write(Utf8JsonWriter writer, TemporaryCirStatements value, JsonSerializerOptions options)
        => value.Write(writer, options);
}

public sealed class TemporaryCirExpressionsConverter : JsonConverter<TemporaryCirExpressions>
{
    public override bool HandleNull => true;
    public override TemporaryCirExpressions Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => new(JsonDocument.ParseValue(ref reader));
    public override void Write(Utf8JsonWriter writer, TemporaryCirExpressions value, JsonSerializerOptions options)
        => value.Write(writer, options);
}

public sealed class TemporaryCirExpressionConverter : JsonConverter<TemporaryCirExpression>
{
    public override bool HandleNull => true;
    public override TemporaryCirExpression Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => new(JsonDocument.ParseValue(ref reader));
    public override void Write(Utf8JsonWriter writer, TemporaryCirExpression value, JsonSerializerOptions options)
        => value.Write(writer, options);
}
