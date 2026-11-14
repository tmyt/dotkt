using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    // Selection stays shared with the live DOM consumers. Materialization uses
    // owning typed values directly, never a JSON projection of the operand.
    internal static IBirExpression CoerceForFixedSlot(
        IBirExpression value, TypeNode src, TypeNode target, ValueTypeOracle isValue, ReferenceMetadataIndex refs,
        bool exactPropertyTarget = false)
    {
        if (value is not BirGraphNode graph) return null;
        var selection = SelectFixedSlotCoercion(graph.Tag,
            value is BirCast cast ? cast.Type.OrDefault() : null,
            src, target, exactPropertyTarget, isValue, refs);
        if (selection.Kind == SlotCoercionKind.None) return null;
        var operand = OptionalField<IBirExpression>.Of((IBirExpression)graph.DeepClone());
        var type = BirGraphValue.CopyType(selection.Type);
        return selection.Kind switch {
            SlotCoercionKind.NullableWrap => new BirNullableWrap { Elem = type, E = operand },
            SlotCoercionKind.NullableValue => new BirNullableValue { Elem = type, E = operand },
            SlotCoercionKind.Cast => new BirCast { Type = type, E = operand },
            _ => throw new InvalidOperationException("Unknown fixed-slot coercion"),
        };
    }

    internal static void SelfTestSourceFixedSlots()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var erased = new TypeNode.Fqn("object");
        var variable = new TypeNode.Tv("method", 0);
        var nullable = new TypeNode.Nullable(integer);
        var value = new BirLocal { Name = "value", Sty = integer };
        bool IsValue(TypeNode.Fqn type) => type.Name is "kotlin.Int" or "Value";
        IBirExpression Coerce(TypeNode source, TypeNode target, IBirExpression operand = null)
            => CoerceForFixedSlot(operand ?? value, source, target, IsValue, refs: null);
        void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Typed fixed-slot coercion: " + reason);
        }

        Require(Coerce(new TypeNode.Oblivious(integer), nullable) is BirNullableWrap {
                Elem.Value: var wrapType, E.Value: BirLocal { Name.Value: "value" } wrapped,
            } && wrapType == integer && !ReferenceEquals(wrapped, value), "nullable construction/operand ownership");
        Require(Coerce(nullable, new TypeNode.Oblivious(integer)) is BirNullableValue {
                Elem.Value: var unwrapType,
            } && unwrapType == integer, "nullable extraction");
        Require(Coerce(erased, variable) is BirCast { Type.Value: var narrowed } && narrowed == variable
            && Coerce(variable, erased) is BirCast { Type.Value: var boxed } && boxed == erased,
            "type-variable object seam");
        Require(Coerce(text, erased) == null && Coerce(integer, integer) == null
            && Coerce(erased, new TypeNode.ByRef(integer)) == null,
            "reference widening, matching slots or managed-reference storage was converted");
        var alreadyCast = new BirCast { Type = integer, E = value };
        Require(Coerce(erased, integer, alreadyCast) == null,
            "an already-targeted object narrowing was wrapped twice");
        Require(Coerce(erased, integer, new BirThrowExpression { Value = value }) == null,
            "a terminating expression was assigned a stack conversion");

        var sourceArgument = new TypeNode.Fqn("Argument", new TypeNode[] { variable });
        var constructed = new TypeNode.Fqn("Value", new TypeNode[] { sourceArgument });
        var sameSlot = new TypeNode.Fqn("Value", new TypeNode[] {
            new TypeNode.Fqn("Argument", new TypeNode[] { new TypeNode.Tv("method", 0) }),
        });
        var constructedWrap = Coerce(constructed, new TypeNode.Nullable(sameSlot));
        Require(constructedWrap is BirNullableWrap { Elem.Value: TypeNode.Fqn { Args.Length: 1 } copied }
            && !ReferenceEquals(copied.Args, sameSlot.Args), "complete constructed value slot/owned type vector");

        var targetArray = new TypeNode.Array(new TypeNode.Nullable(text));
        Require(Coerce(new TypeNode.Array(erased), targetArray) is BirCast { Type.Value: var array }
            && TypeNodeDataEquality.Equal(array, targetArray), "reference-array checked projection");
        Require(Coerce(new TypeNode.Fqn("Holder", new TypeNode[] { erased }),
            new TypeNode.Fqn("Holder", new TypeNode[] { text })) == null,
            "an invariant nested generic difference was treated as an object seam");
        var references = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var nestedSource = new TypeNode.Fqn("System.Environment.SpecialFolder");
        var nestedPhysical = new TypeNode.Fqn("System.Environment+SpecialFolder");
        var nullableNested = new TypeNode.Nullable(nestedPhysical);
        bool NestedValue(TypeNode.Fqn type) => type.Name is "System.Environment.SpecialFolder" or "System.Environment+SpecialFolder";
        Require(CoerceForFixedSlot(value, nestedSource, nullableNested, NestedValue, references) is BirNullableWrap
            && CoerceForFixedSlot(value, nestedSource, nullableNested, NestedValue, refs: null) == null,
            "fixed-slot classifier identity must come from the explicitly supplied reference universe");
        var values = new TypeNode.Array(new TypeNode.Fqn("Value"));
        Require(CoerceForFixedSlot(value, new TypeNode.Array(erased), values, IsValue, references) == null
            && CoerceForFixedSlot(value, new TypeNode.Array(erased), values, _ => false, references) is BirCast,
            "array projection must use the supplied value oracle rather than the previous nullable pass's state");
        Console.WriteLine("[typed fixed slots] self-test OK (nullable, object and array seams, storage, termination, ownership and explicit reference/value authorities)");
    }
}
