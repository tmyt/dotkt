import NestedArityInterop.Outer
import NestedArityInterop.Oracle
import NestedArityInterop.ConcreteNullableSlot
import NestedArityInterop.EventValueItem
import NestedArityInterop.NullableSlot
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private enum class NestedSlotEnum { VALUE }

private class NestedValueField(initial: Outer.ValueItem<String>) {
    @ClrField var value: Outer.ValueItem<String> = initial
}

private var nestedStaticValue = Outer.ValueItem("initial static")
    set(value) {
        val nullable: Outer.ValueItem<String>? = value
        field = nullable!!
    }

private fun returnNestedValueFromExpression(
    value: Outer.ValueItem<String>?,
    takeFallthrough: Boolean,
): Outer.ValueItem<String> {
    val selected = if (takeFallthrough) value else return value!!
    return selected!!
}

class NestedArityTests {
    @TestAttribute
    fun nestedClassifiersThatDifferOnlyByArityRemainDistinct() {
        assertEquals(1, Outer.Item().Value)
        assertEquals("generic", Outer.Item1("generic").Value)

        // A reference-metadata TypeNode uses dotted nesting (`Outer.ValueItem`), while reflection identifies the
        // declaration as `Outer+ValueItem`1`. The nullable wrapper must survive because the dotted classifier is a
        // struct, independently of which producer minted its token.
        assertEquals(true, Oracle.HasNestedValue(Oracle.NestedValue()))

        // A bare external struct construction entering a nullable local must be wrapped as Nullable<T>; the `!!`
        // present branch must then unwrap that same value before calling the struct member.
        val localValue: Outer.ValueItem<String>? = Outer.ValueItem("local nested value")
        assertEquals("local nested value", localValue!!.Value)

        // CLR permits class Kind and struct Kind<T> in one scope. dll2klib projects the latter as Kind1; the arity is
        // part of the oracle identity, so the class remains an NRT reference and the constructed struct remains a
        // structural Nullable<T> rather than whichever declaration happened to be scanned last.
        assertEquals(true, Oracle.HasReferenceKind(Oracle.ReferenceKind()))
        assertEquals(true, Oracle.HasValueKind(Oracle.ValueKind()))

        // Nested CLR declarations flatten the outer and inner generic arguments into one identity. The producer and
        // the BIR consumer must therefore agree that GenericOuter<Int>.Leaf<String> has arity two.
        assertEquals(true, Oracle.FlattenedNestedValue() != null)
        assertEquals(true, Oracle.HasFlattenedNestedValue(Oracle.FlattenedNestedValue()))
    }

    @TestAttribute
    fun externalValueExpressionReturnUsesStructuralNullableConversion() {
        val localValue: Outer.ValueItem<String>? = Outer.ValueItem("local nested value")
        assertEquals("local nested value", returnNestedValueFromExpression(localValue, false).Value)
    }

    @TestAttribute
    fun externalValueFieldUsesStructuralNullableConversion() {
        val localValue: Outer.ValueItem<String>? = Outer.ValueItem("local nested value")
        val field = NestedValueField(Outer.ValueItem("initial field"))
        field.value = localValue!!
        assertEquals("local nested value", field.value.Value)
    }

    @TestAttribute
    fun externalValueStaticFieldUsesStructuralNullableConversion() {
        nestedStaticValue = Outer.ValueItem("updated static")
        assertEquals("updated static", nestedStaticValue.Value)
    }

    @TestAttribute
    fun clrNullablePropertyWrapsLocalEnum() {
        val nullableSlot = NullableSlot<NestedSlotEnum>()
        nullableSlot.Value = NestedSlotEnum.VALUE
        assertEquals(NestedSlotEnum.VALUE, nullableSlot.Value!!)
    }

    @TestAttribute
    fun concreteClrNullablePropertyWrapsPlatformStruct() {
        val nullableSlot = ConcreteNullableSlot()
        nullableSlot.Value = Oracle.PlatformNestedValue()
        assertEquals("platform nested value", nullableSlot.Value!!.Value)
    }

    @TestAttribute
    fun absentExternalValueKeepsKotlinNullChecks() {
        val absentValue: Outer.ValueItem<String>? = null
        val npe = try { absentValue!!.Value; "no" } catch (_: NullPointerException) { "npe" }
        assertEquals("npe", npe)
        assertEquals(true, absentValue?.Value == null)
    }

    @TestAttribute
    fun nullableExternalValueEventReceiverUsesBareSpill() {
        // Event subscription binding synthesizes its receiver spill after the first use-axis walk. The conditional
        // second walk must unwrap the nullable struct before that bare-value local is stored and dispatched through.
        val eventValue: EventValueItem? = Oracle.EventValue()
        val subscription = eventValue!!.Changed.subscribe { _ -> }
        subscription.close()
    }
}
