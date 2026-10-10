@file:Suppress("UNCHECKED_CAST")
package roundtriptests.nativevalueframe

import NUnit.Framework.TestAttribute
import GenericValueInterop.GenericFrameApi
import GenericValueInterop.NativeCell
import GenericValueInterop.NativeValueEcho
import kotlin.clr.byref
import kotlin.clr.ClrRef

abstract class Anchor<S : Anchor<S>>(val tag: String)
class Leaf<E>(tag: String) : Anchor<Leaf<E>>(tag)
private class Stored<T>(val value: T)
private class StoredNativeValueEcho : NativeValueEcho {
    override fun <T> Echo(value: T): T = Stored(value).value
}
private fun <T : Appendable> appendNativeBound(value: T): T {
    value.append('x')
    return value
}
private class NativeResultStore<T>(private val value: T) {
    fun read(): T = NativeCell<T>(value).Read()
    fun readProperty(): T = NativeCell<T>(value).Value
}
private class NativeFieldStore<T>(var value: T) {
    fun replace(replacement: T): T {
        val observed = GenericFrameApi.SetAndObserve<T>(byref(value), byref(value), replacement)
        check(observed == value)
        return value
    }
}

private fun <S : Anchor<S>> replace(value: S, replacement: S): String {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<S>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return slot.tag
}

private fun <T> replaceUnbounded(value: T, replacement: T): T {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return slot
}

private fun <T> storeAndReplace(value: T, replacement: T): T {
    val stored = Stored(value)
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    check((stored.value as Any) === (value as Any))
    return slot
}

private fun <T> replaceAndStore(value: T, replacement: T): Stored<T> {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return Stored(slot)
}

private fun <T> writeAndReplace(value: T, before: T, replacement: T, after: T): Stored<T> {
    var slot = value
    slot = before
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any?) === (slot as Any?))
    slot = after
    return Stored(slot)
}

private fun <T> setAndStore(value: T, replacement: T): Stored<T> {
    var slot = value
    val observed = GenericFrameApi.SetAndObserve<T>(byref(slot), byref(slot), replacement)
    check(observed == slot)
    return Stored(slot)
}

private fun <T> replaceReference(slot: ClrRef<T>, replacement: T): Stored<T> {
    slot.value = replacement
    return Stored(slot.value)
}

private fun <T> replaceLiveReference(value: T, replacement: T): Stored<T> {
    var slot = value
    var live by byref(GenericFrameApi.Reference<T>(byref(slot)))
    live = replacement
    val stored = Stored(live)
    check(live == slot)
    return stored
}

private fun <T> passReference(value: T, replacement: T): Stored<T> {
    val stored = Stored(value)
    var slot = value
    val observed = replaceReference<T>(byref(slot), replacement)
    check(observed.value == slot) { "managed-reference write was not observed" }
    check(stored.value == value) { "the original stored value changed" }
    return Stored(slot)
}

private class StoredCollection<T>(value: T) : AbstractMutableCollection<T>() {
    private var item: T = value
    override val size: Int get() = 1
    override fun iterator(): MutableIterator<T> = throw IllegalStateException()
    override fun add(element: T): Boolean {
        item = element
        return true
    }
    fun current(): T = item
}

class NativeValueFramePreservationTests {
    @TestAttribute
    fun nativeGenericDispatchKeepsItsPublishedFrame() {
        val echo: NativeValueEcho = StoredNativeValueEcho()
        check(echo.Echo("hello") == "hello")
        check(echo.Echo(42) == 42)
    }

    @TestAttribute
    fun nativeRefAndOutAliasTheGenericFieldItself() {
        val text = NativeFieldStore("initial")
        text.value = "before"
        check(text.replace("replacement") == "replacement")
        check(text.value == "replacement")
        text.value = "after"
        check(text.value == "after")
        val number = NativeFieldStore(10)
        check(number.replace(42) == 42)
        check(number.value == 42)
    }

    @TestAttribute
    fun aNativeFieldChecksOrdinaryWritesWithoutChangingKotlinCastSemantics() {
        val slot = NativeFieldStore(Leaf<String>("initial"))
        val incompatible = Leaf<Int>("incompatible") as Leaf<String>
        var rejected = false
        try {
            slot.value = incompatible
        } catch (_: System.InvalidCastException) {
            rejected = true
        }
        check(rejected)
        check(slot.value.tag == "initial")
        check(slot.replace(Leaf<String>("replacement")).tag == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideABoundedHelperPreservesItsExactSlot() {
        check(replace(Leaf<String>("initial"), Leaf<String>("replacement")) == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideAnUnboundedHelperPreservesItsExactSlot() {
        check(replaceUnbounded("initial", "replacement") == "replacement")
    }

    @TestAttribute
    fun nativeRefAndKotlinStorageUseTheSameLogicalArgument() {
        check(storeAndReplace("initial", "replacement") == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideAGenericFactoryPreservesItsExactSlot() {
        check(replaceAndStore("initial", "replacement").value == "replacement")
    }

    @TestAttribute
    fun ordinaryWritesBeforeAndAfterNativeRefUseTheSameLocation() {
        check(writeAndReplace("initial", "before", "replacement", "after").value == "after")
    }

    @TestAttribute
    fun nativeRefAndOutPreserveAliasingWithValueTypes() {
        check(setAndStore(10, 42).value == 42)
    }

    @TestAttribute
    fun nativeRefAndOutPreserveAliasingWithNullableValues() {
        check(setAndStore<String?>("initial", null).value == null)
        check(setAndStore<String?>(null, "replacement").value == "replacement")
    }

    @TestAttribute
    fun storingAValuePreservesTheInheritedCollectionSlot() {
        val values = StoredCollection("initial")
        values.add("replacement")
        check(values.current() == "replacement")
        val base: AbstractMutableCollection<String> = values
        base.add("through-base")
        check(values.current() == "through-base")
    }

    @TestAttribute
    fun aManagedReferenceParameterKeepsItsNativeElementType() {
        var slot = "initial"
        check(replaceReference(byref(slot), "replacement").value == "replacement")
        check(slot == "replacement")
    }

    @TestAttribute
    fun aLiveReferenceKeepsTheOriginalLocalLocation() {
        check(replaceLiveReference("initial", "replacement").value == "replacement")
        check(replaceLiveReference(10, 42).value == 42)
    }

    @TestAttribute
    fun aGenericKotlinHelperPassesTheExactManagedReferenceLocation() {
        check(passReference("initial", "replacement").value == "replacement")
        check(passReference(10, 42).value == 42)
    }

    @TestAttribute
    fun aNativeResultIsConvertedToTheKotlinStorageRole() {
        check(NativeResultStore("value").read() == "value")
        check(NativeResultStore(42).read() == 42)
        check(NativeResultStore("value").readProperty() == "value")
        check(NativeResultStore(42).readProperty() == 42)
    }

    @TestAttribute
    fun aStoredNativeClassBoundKeepsItsReceiverIdentity() {
        val value = StringBuilder()
        check(appendNativeBound(value) === value)
        check(value.toString() == "x")
    }

    @TestAttribute
    fun anUncheckedKotlinCastDoesNotRelaxTheNativeLocationType() {
        // The ordinary Kotlin cast keeps its raw-classifier semantics. The
        // subsequent native location requires the exact CLR construction.
        val incompatible = Leaf<Int>("initial") as Leaf<String>
        var rejected = false
        try {
            replaceAndStore(incompatible, Leaf<String>("replacement"))
        } catch (_: System.InvalidCastException) {
            rejected = true
        }
        check(rejected) { "an incompatible CLR construction entered a native ref location" }
    }
}
