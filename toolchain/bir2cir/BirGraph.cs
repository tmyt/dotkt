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
    public virtual TypeNode? CallBindingTypeHint => null;
    public virtual OptionalField<string> DeclarationIdentityFact => default;
    public virtual OptionalField<TypeNode[]> ApplicationTypeArguments
    {
        get => default;
        set => throw new InvalidOperationException("This graph occurrence has no application type arguments");
    }
    public virtual OptionalField<TypeNode> SuspendFunctionTypeFact
    {
        get => default;
        set => throw new InvalidOperationException("This graph occurrence has no saved suspend function type");
    }
    public virtual OptionalField<TypeNode> SuspendFunctionReturnFact
    {
        get => default;
        set => throw new InvalidOperationException("This graph occurrence has no saved suspend return type");
    }
    public abstract void VisitChildren(Action<BirGraphValue> visit);
    public abstract void VisitChildSlots(Action<BirGraphSlot> visit);
    // Source recognition extends declaration parameters and walks each schema
    // statement vector in its own preceding-declaration environment.
    public virtual void VisitSourceParameters(Action<string, TypeNode> visit) { }
    public virtual void VisitSourceChildren(Action<BirGraphSlot> child, Action<BirSourceRegion> region)
        => VisitChildSlots(child);
    // Construction operands execute in the caller; a nested declaration's body
    // does not. The maintained frame profile generates the latter overrides.
    public virtual void VisitCurrentFrameSlots(Action<BirGraphSlot> visit) => VisitChildSlots(visit);
    public void Visit(Action<BirGraphValue> visit)
    {
        Action<BirGraphValue> walk = null!;
        walk = node => { visit(node); node.VisitChildren(walk); };
        walk(this);
    }
    public abstract BirGraphValue RewriteChildren(Func<BirGraphValue, BirGraphValue> rewrite);
    // Snapshot ownership is stronger than Rewrite: every mutable graph/vector
    // occurrence, including opaque byte arrays and nested type arguments, copies.
    public abstract BirGraphValue DeepClone();
    // Structured type slots only. Frozen source facts encoded as strings in the
    // maintained schema are opaque to this operation, as they were on the wire.
    public abstract BirGraphValue RewriteTypeFields(Func<TypeNode, TypeNode> rewrite);
    public abstract void RewriteTypeFieldsInPlace(Func<TypeNode, TypeNode> rewrite);
    public abstract bool StructurallyEquals(BirGraphValue other);
    // The schema states which declaration owns each type-bearing occurrence.
    // Consumers choose their traversal policy; the model does not perform an
    // inline substitution or infer a frame from a field's physical spelling.
    public abstract BirGraphValue RewriteGenericFrame(
        Func<BirGenericFrameRole, TypeNode, TypeNode> type,
        Func<BirGenericFrameRole, BirGraphValue, BirGraphValue> child);
    public abstract void RewriteGenericFrameInPlace(
        Func<BirGenericFrameRole, TypeNode, TypeNode> type,
        Action<BirGenericFrameRole, BirGraphValue> child);
    public abstract void RewriteRepresentationInPlace(
        Func<BirGenericFrameRole, BirRepresentationRole, TypeNode, TypeNode> type,
        Action<BirGenericFrameRole, BirRepresentationRole, BirGraphValue> child);
    public virtual void RewriteRepresentationParametersInPlace(
        Func<BirRepresentationParameterRole, BirTypeParameter[], BirTypeParameter[]> rewrite) { }
    public abstract void WriteJson(Utf8JsonWriter writer);
    // Temporary frontier to the not-yet-migrated owning passes. It builds
    // nodes directly: no serialized child, parse, or generic extension-data bag.
    public abstract JsonObject ToDom();
    public BirGraphValue Rewrite(Func<BirGraphValue, BirGraphValue> rewrite)
        => rewrite(RewriteChildren(child => child.Rewrite(rewrite)));

    protected static bool EqualField<T>(OptionalField<T> left, OptionalField<T> right, Func<T, T, bool> equal)
        => left.IsPresent == right.IsPresent && left.IsNull == right.IsNull
            && (!left.HasValue || equal(left.Value, right.Value));

    protected static bool EqualItems<T>(T[]? left, T[]? right, Func<T, T, bool> equal)
        => ReferenceEquals(left, right) || left is not null && right is not null
            && left.Length == right.Length && left.Zip(right).All(pair => equal(pair.First, pair.Second));

    protected static bool EqualFrame(NullableRepresentationFrame left, NullableRepresentationFrame right)
        => left.SourceArity == right.SourceArity && left.NullableIndices.SequenceEqual(right.NullableIndices)
            && left.StorageIndices.SequenceEqual(right.StorageIndices)
            && left.NullableStorageIndices.SequenceEqual(right.NullableStorageIndices)
            && left.PhysicalOrder.SequenceEqual(right.PhysicalOrder);

    internal static TypeNode CopyType(TypeNode type) => type switch {
        TypeNode.Fqn value => value with {
            Args = value.Args?.Select(CopyType).ToArray(),
            Classifier = value.Classifier is { } classifier
                ? classifier with { OwnerPath = classifier.OwnerPath.ToArray() } : null,
        },
        TypeNode.Fn value => value with { Ret = CopyType(value.Ret), Params = value.Params.Select(CopyType).ToArray(),
            Recv = value.Recv is { } receiver ? CopyType(receiver) : null, Ctx = value.Ctx?.Select(CopyType).ToArray() },
        TypeNode.FnPtr value => value with { Ret = CopyType(value.Ret), Params = value.Params.Select(CopyType).ToArray() },
        TypeNode.Projection value => value with { Of = CopyType(value.Of) },
        TypeNode.Nullable value => value with { Of = CopyType(value.Of) },
        TypeNode.Oblivious value => value with { Of = CopyType(value.Of) },
        TypeNode.Array value => value with { Elem = CopyType(value.Elem) },
        TypeNode.ByRef value => value with { Of = CopyType(value.Of) },
        TypeNode.Ptr value => value with { Of = CopyType(value.Of) },
        TypeNode.Mod value => value with { M = CopyType(value.M), Of = CopyType(value.Of) },
        _ => type,
    };
}

internal abstract record BirGraphNode : BirGraphValue
{
    public abstract string Tag { get; }
}

internal enum BirGenericFrameRole
{
    Lexical,
    ReferencedDeclaration,
    ResolvedDeclaration,
    OverrideDeclaration,
    SharedCaptureDeclaration,
    LocalDeclaration,
    SyntheticDeclaration,
    SyntheticApplication,
    FrozenSourceFact,
    StorageSourceType,
}

internal enum BirRepresentationRole
{
    Lexical, ValueType, TypeArguments, Element, Return, ResultStamp, DeclaredMemberType,
    Declaration, Constraint, Foreign, TypeParameterDeclarations, Attributes, InheritedImplementation,
}

internal enum BirRepresentationParameterRole { Owner, Method, SharedCell }

// A short-lived address into an owning typed graph, not a string-key property
// view. Generated setters retain each declared child role. Replacements change
// the owning occurrence; use Rewrite when taking an independent graph snapshot.
internal readonly record struct BirGraphSlot(Func<BirGraphValue> Read, Action<BirGraphValue> Replace)
{
    public BirGraphValue Value => Read();
}

// A result belongs to the final environment of its own statement sequence,
// not to another region or to the surrounding declaration's environment.
internal readonly record struct BirSourceRegion(IBirStatement[] Statements, BirGraphSlot? Result = null);

internal interface IBirExpression : IBirAttributeValue;
internal interface IBirStatement;
internal interface IBirTypeDeclaration
{
    OptionalField<string> Name { get; set; }
    OptionalField<string> Kind { get; set; }
    OptionalField<bool> Generated { get; set; }
    OptionalField<string> SemanticOwner { get; set; }
    OptionalField<string> NestedIn { get; set; }
    OptionalField<string> Vis { get; set; }
    OptionalField<BirModifiers> Mods { get; set; }
    OptionalField<BirTypeParameter[]> TypeParams { get; set; }
    OptionalField<BirTypeParameter[]> CapturedTypeParams { get; set; }
    OptionalField<TypeNode> Base { get; set; }
    OptionalField<TypeNode[]> Interfaces { get; set; }
    OptionalField<BirFieldDeclaration[]> Fields { get; set; }
    OptionalField<BirConstructor[]> Ctors { get; set; }
    OptionalField<BirFunctionDeclaration[]> Methods { get; set; }
    OptionalField<BirPropertyDeclaration[]> Properties { get; set; }
    OptionalField<BirClrEventBacking[]> ClrEvents { get; set; }
    OptionalField<BirAttribute[]> Attrs { get; set; }
    OptionalField<BirParameter[]> Params { get; set; }
    OptionalField<TypeNode> Ret { get; set; }
    OptionalField<BirStaticCarrier> StaticCarrier { get; set; }
    OptionalField<BirCompanionCarrier> CompanionCarrier { get; set; }
}
internal interface IBirAttributeValue;
// A saved source graph is metadata, not another live source declaration for
// declaration-level capture or representation passes to reinterpret.
internal interface IBirExecutablePayload;

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

    static void Seen(ref UInt128 seen, UInt128 field)
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
