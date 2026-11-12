#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Cir;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DotKt.Tests.BirGraph")]

namespace DotKt.Bir.Lowering;

// These are owning values, not a view over JSON. Phase-specific facts are explicit
// properties generated from source and physical profiles. Presence is retained
// while passes establish/remove facts; no missing declaration fact is inferred.
internal abstract record BirGraphValue
{
    public virtual TypeNode? ResultTypeStamp => null;
    public abstract void VisitChildren(Action<BirGraphValue> visit);
    public void Visit(Action<BirGraphValue> visit)
    {
        visit(this);
        VisitChildren(child => child.Visit(visit));
    }
    public abstract BirGraphValue RewriteChildren(Func<BirGraphValue, BirGraphValue> rewrite);
    public abstract void WriteJson(Utf8JsonWriter writer);
    // Temporary one-way frontier to the not-yet-migrated owning passes. It builds
    // nodes directly: no serialized child, parse, or generic extension-data bag.
    public abstract JsonObject ToDom();
    public BirGraphValue Rewrite(Func<BirGraphValue, BirGraphValue> rewrite)
        => rewrite(RewriteChildren(child => child.Rewrite(rewrite)));
}

internal abstract record BirGraphNode : BirGraphValue
{
    public abstract string Tag { get; }
}

internal interface IBirExpression : IBirAttributeValue;
internal interface IBirStatement;
internal interface IBirTypeDeclaration;
internal interface IBirAttributeValue;

internal static partial class BirGraphJson
{
    delegate T Reader<T>(ref Utf8JsonReader reader);

    internal static BirFileDeclaration ReadFile(byte[] content)
        => BirCarrier.DecodeBody(BirCarrier.JsonV1, content, ReadBirFileDeclaration);

    internal static void WriteOptional<T>(Utf8JsonWriter writer, string name, OptionalField<T> field,
        Action<Utf8JsonWriter, T> write)
    {
        if (!field.IsPresent) return;
        writer.WritePropertyName(name);
        if (field.IsNull) writer.WriteNullValue();
        else write(writer, field.Value);
    }

    // The old owning DOM consumers call GetValue<int/long/double> on numeric
    // tokens. Retain that token behavior at this temporary leaf frontier, without
    // rebuilding/parsing an enclosing executable tree or choosing a CLR type.
    internal static JsonNode? LiteralToDom(BirLiteral value) => value switch {
        BirLiteral.IntegerLiteral number => JsonValue.Create(JsonSerializer.SerializeToElement(number.Value)),
        BirLiteral.UnsignedIntegerLiteral number => JsonValue.Create(JsonSerializer.SerializeToElement(number.Value)),
        BirLiteral.RealLiteral number => JsonValue.Create(JsonSerializer.SerializeToElement(number.Value)),
        _ => value.ToDom(),
    };

    static T[] ReadArray<T>(ref Utf8JsonReader reader, Reader<T> read)
    {
        Require(reader, JsonTokenType.StartArray);
        var values = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            values.Add(read(ref reader));
        Require(reader, JsonTokenType.EndArray);
        return values.ToArray();
    }

    static string ReadString(ref Utf8JsonReader reader)
    {
        Require(reader, JsonTokenType.String);
        return reader.GetString()!;
    }

    static TypeNode ReadFrozenType(ref Utf8JsonReader reader) => TypeNode.Parse(ReadString(ref reader));

    static void Require(Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type) throw new FormatException();
    }

    static void Seen(ref ulong seen, ulong field)
    {
        if ((seen & field) != 0) throw new FormatException();
        seen |= field;
    }

    static string Discriminator(Utf8JsonReader reader, string name)
    {
        Require(reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Require(reader, JsonTokenType.PropertyName);
            var property = reader.GetString();
            if (!reader.Read()) throw new FormatException();
            if (property == name) return ReadString(ref reader);
            reader.Skip();
        }
        throw new FormatException();
    }

    static bool HasProperty(Utf8JsonReader reader, string name)
    {
        Require(reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Require(reader, JsonTokenType.PropertyName);
            var property = reader.GetString();
            if (!reader.Read()) throw new FormatException();
            if (property == name) return true;
            reader.Skip();
        }
        return false;
    }
}
