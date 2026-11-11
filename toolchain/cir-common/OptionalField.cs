#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotKt.Cir;

// A declaration field's presence is data: absent, explicit null, and a value are distinct.
[JsonConverter(typeof(OptionalFieldConverterFactory))]
public readonly record struct OptionalField<T>
{
    readonly byte _state;
    readonly T? _value;
    OptionalField(byte state, T? value) { _state = state; _value = value; }
    public bool IsPresent => _state != 0;
    public bool IsNull => _state == 1;
    public bool HasValue => _state == 2;
    public T Value => HasValue ? _value! : throw new InvalidOperationException("Field has no value");
    public T? OrDefault(T? fallback = default) => HasValue ? _value : fallback;
    public static OptionalField<T> Null => new(1, default);
    public static OptionalField<T> Of(T? value) => value is null ? Null : new(2, value);
    public static implicit operator OptionalField<T>(T value) => Of(value);
}

public static class OptionalFieldArrays
{
    public static T[] OrEmpty<T>(this OptionalField<T[]> field) => field.OrDefault() ?? Array.Empty<T>();
}

public sealed class OptionalFieldConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(OptionalField<>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(OptionalFieldConverter<>).MakeGenericType(type.GetGenericArguments()))!;
}

public sealed class OptionalFieldConverter<T> : JsonConverter<OptionalField<T>>
{
    public override bool HandleNull => true;
    public override OptionalField<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? OptionalField<T>.Null
            : OptionalField<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options));
    public override void Write(Utf8JsonWriter writer, OptionalField<T> value, JsonSerializerOptions options)
    {
        if (!value.IsPresent) throw new InvalidOperationException("Absent fields must be omitted by the containing record");
        if (value.IsNull) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value.Value, options);
    }
}
