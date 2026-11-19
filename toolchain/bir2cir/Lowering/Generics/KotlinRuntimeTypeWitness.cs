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

    internal static IBirExpression FlagsValue(IBirExpression witness) => FieldValue(witness, 0, new TypeNode.Fqn("System.Int32"));
    internal static IBirExpression ClassifierValue(IBirExpression witness) => FieldValue(witness, 1, new TypeNode.Fqn("System.Type"));
    internal static IBirExpression PredicateValue(IBirExpression witness) => FieldValue(witness, 2, PredicateType);
    internal static IBirExpression ReflectedValue(IBirExpression witness) => FieldValue(witness, 3, new TypeNode.Fqn("System.Object"));
    static IBirExpression FieldValue(IBirExpression witness, int index, TypeNode result)
    {
        // These constructors are compiler-authored witnesses. Reading scalar facts must
        // not allocate the unused KType, even when the same method reflects elsewhere.
        if (witness is BirNew tuple)
            return (IBirExpression)((BirGraphValue)tuple.Args.Value[index]).DeepClone();
        return new BirFieldGet {
            OwnerType = Type, Recv = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)witness).DeepClone()),
            Name = "Item" + (index + 1), Type = result, Sty = BirGraphValue.CopyType(result),
        };
    }

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
