import NUnit.Framework.TestAttribute
import kotlin.clr.byref
import roundtrip.clrrefparameters.ReferenceBox
import roundtrip.innerselection.*

private fun <T> importedGenericConstructorSelection(outer: NamedConstructorOuter<Any, T>, value: T): Int =
    outer.Entry(value = value).selection + outer.GenericEntry<Int>(value = value, other = 42).selection

class NamedInnerConstructorSelectionTests {
    @TestAttribute
    fun namedArgumentsKeepClosedAndErasedConstructorOverloadsDistinct() {
        check(sameModuleNamedConstructorSelection())
        val outer: NamedConstructorOuter<Any, String> = NamedConstructorOuter<Int, String>()
        check(outer.Entry(value = "x").selection == 1)
        check(outer.Entry(text = "x").selection == 2)
        check(outer.AnyEntry(value = "x").selection == 1)
        check(outer.AnyEntry(any = null).selection == 2)
        check(outer.NullableEntry(value = "x").selection == 1)
        check(outer.NullableEntry(nullable = null).selection == 2)
    }

    @TestAttribute
    fun genericCallersKeepSelectedConstructorsWhenInstantiatedWithString() {
        val outer: NamedConstructorOuter<Any, String> = NamedConstructorOuter<Int, String>()
        check(importedGenericConstructorSelection(outer, "x") == 2)
        check(sameModuleGenericConstructorSelection(outer, "x") == 2)
        check(outer.GenericEntry<Int>(value = "x", other = 42).selection == 1)
        check(outer.GenericEntry<Int>(text = "x", other = 42).selection == 2)
    }

    @TestAttribute
    fun selectedReferenceConstructorsKeepExactOperationalContracts() {
        val outer: NamedConstructorOuter<Any, String> = NamedConstructorOuter<Int, String>()
        var slot = ReferenceBox("before")
        val first = ReferenceBox("first")
        val second = ReferenceBox("second")
        check(outer.ReferenceEntry(value = byref(slot), replacement = first).selection == 1)
        check(slot === first)
        check(outer.ReferenceEntry(text = byref(slot), replacement = second).selection == 2)
        check(slot === second)
    }
}
