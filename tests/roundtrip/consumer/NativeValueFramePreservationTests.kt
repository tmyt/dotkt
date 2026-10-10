package roundtriptests.nativevalueframe

import NUnit.Framework.TestAttribute
import GenericValueInterop.GenericFrameApi
import kotlin.clr.byref

abstract class Anchor<S : Anchor<S>>(val tag: String)
class Leaf<E>(tag: String) : Anchor<Leaf<E>>(tag)

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

class NativeValueFramePreservationTests {
    @TestAttribute
    fun nativeRefInsideABoundedHelperPreservesItsExactSlot() {
        check(replace(Leaf<String>("initial"), Leaf<String>("replacement")) == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideAnUnboundedHelperPreservesItsExactSlot() {
        check(replaceUnbounded("initial", "replacement") == "replacement")
    }
}
