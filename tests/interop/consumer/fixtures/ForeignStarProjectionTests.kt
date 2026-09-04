import ForeignStar.Box
import ForeignStar.Factory
import ForeignStar.IBox
import ForeignStar.IRead
import ForeignStar.Pair
import ForeignStar.CounterCell
import ForeignStar.DerivedReader
import ForeignStar.Duo
import ForeignStar.GenericDerived
import ForeignStar.Inner
import ForeignStar.Outer
import ForeignStar.ReorderedDerived
import ForeignStar.CallableFactory
import ForeignStar.CallableStruct
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private class ForeignProjectedCallableHolder<T> {
    fun read(box: Box<out T>): T = box.Read()
    fun call(factory: CallableFactory<out T>): T = factory.Make().invoke()
    fun callGeneric(factory: CallableFactory<out T>): T = factory.MakeGeneric<Int>().invoke(7)
    fun callStruct(factory: CallableStruct<out T>): T = factory.Make().invoke()
}

class ForeignStarProjectionTests {
    @TestAttribute
    fun arbitraryClrGenericUsesRuntimeClassifierAndExactMember() {
        val value: Any = Factory.StringBoxAsObject()
        assertTrue(value is Box<*>)
        assertFalse(("not a box" as Any) is Box<*>)
        assertTrue((value as? Box<*>) === value)
        assertTrue((null as Any?) is Box<*>?)

        val box = value as Box<*>
        assertEquals("foreign", box.Read())
        // A result containing the projected owner slot is one opaque object. `Holder<String>` is not the fictitious
        // invariant `Holder<Any?>`; reflection must not append a cast to that construction.
        assertEquals("foreign", box.Nested().Value)

        // A nested star makes the entire foreign invariant construction opaque. Outer<Inner<String>> is neither
        // Outer<Any?> nor Outer<Inner<Any?>>, but both member reads remain available through the reflection lane.
        val outer = Factory.NestedOuterAsObject() as Outer<Inner<*>>
        assertEquals("nested-foreign", outer.Value.Read())
        assertEquals("int:4", box.Describe(4))
        assertEquals("string:x", box.Describe("x"))
        assertEquals("Int32:9", box.EchoType<Int>(9))
        try {
            box.Throwing()
            throw IllegalStateException("expected foreign exception")
        } catch (failure: Throwable) {
            assertEquals("foreign-boom", failure.message)
        }
    }

    @TestAttribute
    fun interfaceAndMixedProjectionPreserveIdentityAndReadableSlots() {
        val projectedAlias: List<*> = Factory.DualAliasListAsObject() as List<String>
        assertEquals("alias-string-view", projectedAlias[0])

        val exact = Factory.StringBox()
        val projected: IBox<*> = exact
        assertTrue(projected === exact)
        assertEquals("foreign", projected.Read())

        val pair: Pair<*, String> = Factory.Pair()
        assertEquals(7, pair.First)
        assertEquals("seven", pair.Second)
        pair.Second = "property-changed"
        assertEquals("property-changed", pair.Second)
        assertEquals(7, pair.FirstField)
        pair.SecondField = "changed"
        assertEquals("changed", pair.SecondField)

        // A star-projected foreign struct still has value-copy semantics. Both star locals may initially reference the
        // same box physically, so the reflection lane clones before mutation and writes the new box only to the receiver.
        val first: CounterCell<*> = Factory.CounterCell()
        var second: CounterCell<*> = first
        // A MetadataLoadContext System.Void is not equal to runtime typeof(void). The compiler must classify it by
        // metadata identity and materialize Kotlin's Unit value instead of casting reflection's null result to Unit.
        val incremented: Unit = second.Increment()
        assertEquals(Unit, incremented)
        second.PublicCount = 3
        assertEquals(10, first.ReadCount())
        assertEquals(0, first.PublicCount)
        assertEquals(11, second.ReadCount())
        assertEquals(3, second.PublicCount)

        val dual = Factory.DualRead()
        val stringView: IRead<*> = dual.StringView()
        val intView: IRead<*> = dual.IntView()
        assertEquals("string-view", stringView.ReadView())
        assertEquals(42, intView.ReadView())

        // Owner type parameters are distinct declaration slots. With Duo<*, String>, only the B overload is
        // callable with String; treating both owner parameters as wildcards makes this falsely ambiguous.
        val duo: Duo<*, String> = Factory.Duo()
        assertEquals("second:string", duo.Pick("string"))

        // A generic star owner can inherit its member from an ordinary non-generic base class.
        val derived: DerivedReader<*> = Factory.DerivedReader()
        assertEquals("base", derived.BaseValue())

        // An exact Derived<String> witness must be translated to the member's declaring Base<String> closure.
        val genericDerived: GenericDerived<*> = Factory.GenericDerived()
        assertEquals("derived-view", genericDerived.ReadInherited())

        // Reflection over Base<B> reports B in the derived owner's parameter frame. Runtime member identity must
        // nevertheless use the open Base<T> declaration frame (t0), while the readable B slot remains String.
        val reordered: ReorderedDerived<*, String> = Factory.ReorderedDerived()
        assertEquals("base:value", reordered.PutInherited("value"))
        assertEquals("reordered-view", reordered.ReadInherited())

        val callableFactory: CallableFactory<out String> = Factory.StringCallableFactory()
        assertEquals("callable-result", callableFactory.Make().invoke())
        val holder = ForeignProjectedCallableHolder<String>()
        val exactBox = Factory.StringBoxAsObject() as Box<String>
        assertEquals("foreign", holder.read(exactBox))
        assertEquals("callable-result", holder.call(callableFactory))
        assertEquals("callable-result", holder.callGeneric(callableFactory))
        assertEquals("callable-result", callableFactory.CallableField.invoke())
        assertEquals(null, callableFactory.Maybe(false))
        val actionFactory: CallableFactory<in String> = Factory.ObjectCallableFactory()
        actionFactory.MakeAction().invoke("action")
        val boxedValueFactory: CallableFactory<out Any?> = Factory.IntCallableFactory()
        assertEquals(41, ForeignProjectedCallableHolder<Any?>().call(boxedValueFactory))
        assertEquals(41, boxedValueFactory.CallableField.invoke())
        val boxedValueStruct: CallableStruct<out Any?> = Factory.IntCallableStruct()
        assertEquals(43, ForeignProjectedCallableHolder<Any?>().callStruct(boxedValueStruct))
    }
}
