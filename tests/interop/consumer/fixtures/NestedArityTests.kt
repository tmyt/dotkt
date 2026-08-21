import NestedArityInterop.Outer
import NestedArityInterop.Oracle
import NestedArityInterop.ConcreteNullableSlot
import NestedArityInterop.EventValueItem
import NestedArityInterop.NullableSlot
import NestedArityInterop.SegmentCollisionOuter.Leaf as InnerGenericLeaf
import NestedArityInterop.SegmentCollisionOuter1.Leaf as OuterGenericLeaf
import NestedArityInterop.SegmentCollisionOuter.Contract as InnerGenericContract
import NestedArityInterop.SegmentCollisionOuter1.Contract as OuterGenericContract
import NestedArityInterop.SegmentKindOuter.Kind as InnerGenericKind
import NestedArityInterop.SegmentKindOuter1.Kind as OuterGenericKind
import NestedArityInterop.SameStemShape
import NestedArityInterop.SameStemShape1
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private enum class NestedSlotEnum { VALUE }

private class OuterGenericContractImpl : OuterGenericContract<Int, String>
private class InnerGenericContractImpl : InnerGenericContract<Int, String>
private class SameStemShapeImpl : SameStemShape
private class SameStemGenericShapeImpl : SameStemShape1<String>

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

        // Flattened arity alone cannot identify either of these legal CLR types: one owns a generic slot on each
        // segment, while the other owns both slots on the nested segment. Exercise both directions so the BIR/CIR
        // contract must retain each segment's exact metadata arity instead of selecting by reference order.
        assertEquals(true, Oracle.HasOuterGenericLeaf(Oracle.OuterGenericLeaf()))
        assertEquals(true, Oracle.HasInnerGenericLeaf(Oracle.InnerGenericLeaf()))

        // Exercise each declaration directly. Static Oracle parameters alone prove that a signature type can be
        // transported, but do not cover constructor or property-member lookup through the exact owner index.
        val outerGeneric = OuterGenericLeaf<Int, String>(29, "direct outer generic")
        assertEquals(29, outerGeneric.Outer)
        assertEquals("direct outer generic", outerGeneric.Inner)
        assertEquals(43, outerGeneric.Marker)
        assertEquals("outer:29:direct outer generic", outerGeneric.Describe())
        val innerGeneric = InnerGenericLeaf<Int, String>(31, "direct inner generic")
        assertEquals(31, innerGeneric.Outer)
        assertEquals("direct inner generic", innerGeneric.Inner)
        assertEquals(47, innerGeneric.Marker)
        assertEquals("inner:31:direct inner generic", innerGeneric.Describe())

        var outerEvent = 0
        val outerSubscription = outerGeneric.Changed.subscribe { outerEvent = it }
        outerGeneric.Raise(37)
        assertEquals(37, outerEvent)
        outerSubscription.close()
        var innerEvent = 0
        val innerSubscription = innerGeneric.Changed.subscribe { innerEvent = it }
        innerGeneric.Raise(41)
        assertEquals(41, innerEvent)
        innerSubscription.close()

        val outerContract: OuterGenericContract<Int, String> = OuterGenericContractImpl()
        assertEquals(5, outerContract.Offset)
        val outerDescribe = outerContract::Describe
        assertEquals("outer-contract:43:dim", outerDescribe(43, "dim"))
        val innerContract: InnerGenericContract<Int, String> = InnerGenericContractImpl()
        assertEquals(7, innerContract.Offset)
        val innerDescribe = innerContract::Describe
        assertEquals("inner-contract:47:dim", innerDescribe(47, "dim"))

        // Both closed values can flow through their own star-projected CLR generic. The reflection fallback needs the
        // exact declaring view; comparing arity-free owner names would reuse whichever collision was scanned first.
        val outerStar: OuterGenericLeaf<*, *> = outerGeneric
        assertEquals(43, outerStar.Marker)
        val innerStar: InnerGenericLeaf<*, *> = innerGeneric
        assertEquals(47, innerStar.Marker)

        // The non-generic and generic declarations have distinct inherited graphs even though stripping CLR arity
        // gives both the same owner name. Each local implementer must see only its declaration's own default chain.
        val plainShape: SameStemShape = SameStemShapeImpl()
        assertEquals(53, plainShape.Marker)
        val genericShape: SameStemShape1<String> = SameStemGenericShapeImpl()
        assertEquals("shape", genericShape.Echo("shape"))
        // The generic declaration directly extends its non-generic same-stem sibling. The hierarchy walk must not
        // mistake that cross-arity edge for a metadata cycle.
        assertEquals(53, genericShape.Marker)

        val segmentReference: InnerGenericKind<Int, String>? = null
        assertEquals(true, segmentReference == null)
        val segmentValue: OuterGenericKind<Int, String>? = OuterGenericKind("segment value")
        assertEquals("segment value", segmentValue!!.Value)
        assertEquals(59, Oracle.NeedsNew<OuterGenericKind<Int, String>>())
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
        nullableSlot.Value = null
        assertEquals(true, nullableSlot.Value == null)
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
