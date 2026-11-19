using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A generic value's storage type is not its Kotlin runtime classifier: Int? can be stored as Object.
// Keep the classifier beside the existing nullability/nominal flags in one declaration-owned ABI value.
static partial class KotlinRuntimeTypeWitness
{
    internal const string OperandKey = "kotlinRuntimeTypeWitness";
    internal const string ClassifierKey = "kotlinClassifierRef";
    internal const string PredicateKey = "kotlinClassifierPredicate";
    internal static TypeNode PredicateType => new TypeNode.Fqn("System.Func", new TypeNode[] {
        new TypeNode.Fqn("System.Object"), new TypeNode.Fqn("System.Object"),
    });
    internal static ValueRepresentationLowering ValueRepresentations { get; set; }
    internal static TypeNode Type => new TypeNode.Fqn("System.ValueTuple", new TypeNode[] {
        new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.Type"), PredicateType, new TypeNode.Fqn("System.Object"),
    });
    internal static JsonNode TypeJsonNode() => TypeJson.Write(Type);

    internal static BirFieldGet FlagsValue(IBirExpression witness) => FieldValue(witness, "Item1", new TypeNode.Fqn("System.Int32"));
    internal static BirFieldGet ClassifierValue(IBirExpression witness) => FieldValue(witness, "Item2", new TypeNode.Fqn("System.Type"));
    internal static BirFieldGet PredicateValue(IBirExpression witness) => FieldValue(witness, "Item3", PredicateType);
    internal static BirFieldGet ReflectedValue(IBirExpression witness) => FieldValue(witness, "Item4", new TypeNode.Fqn("System.Object"));
    static BirFieldGet FieldValue(IBirExpression witness, string name, TypeNode result) => new() {
        OwnerType = Type, Recv = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)witness).DeepClone()),
        Name = name, Type = result, Sty = BirGraphValue.CopyType(result),
    };

    internal static BirNew CreateValue(IBirExpression flags, IBirExpression classifier, IBirExpression predicate, IBirExpression reflected = null) => new() {
        Type = Type, Args = new[] { flags, classifier, predicate, reflected ?? new BirConstant { Type = new TypeNode.Fqn("System.Object"), Value = new BirLiteral.NullLiteral() } },
        ArgTypes = ((TypeNode.Fqn)Type).Args,
    };

    internal static IBirExpression KnownValue(TypeNode type, ValueRepresentationLowering values, IBirExpression reflected = null) => CreateValue(
        KotlinTypeWitness.ConstantValue(KotlinTypeWitness.Flags(type)),
        new BirClassReference { Type = BirGraphValue.CopyType(values?.ClassifierType(type) ?? type), KotlinClassifierRef = true },
        values?.PredicateValue(type) ?? new BirConstant { Type = PredicateType, Value = new BirLiteral.NullLiteral() }, reflected);

    internal static IBirExpression NullableValue(IBirExpression witness, SourceHelperBinder reflected = null)
    {
        // An in-place construction is owned here. Move its arguments rather than
        // evaluating the entire reflection descriptor once for every tuple field.
        var items = (witness as BirNew)?.Args.OrDefault();
        var shape = items?[3] ?? ReflectedValue(witness);
        return CreateValue(KotlinTypeWitness.BinaryValue("|", items?[0] ?? FlagsValue(witness), KotlinTypeWitness.ConstantValue(1)),
            items?[1] ?? ClassifierValue(witness), items?[2] ?? PredicateValue(witness),
            reflected == null ? shape : ReflectedKotlinType.MarkNullable(shape, true, reflected));
    }

}
