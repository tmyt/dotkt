package capturedlocalextension

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun capturedExtension(): Int {
    var current = 0
    fun String.replace() { current = length }
    "hello".replace()
    return current
}

private fun <T> capturedGenericReceiver(initial: T, next: T): T {
    var current = initial
    fun T.replaceGeneric() { current = this }
    next.replaceGeneric()
    val bound: () -> Unit = next::replaceGeneric
    initial.replaceGeneric()
    bound()
    return current
}

private class GenericReceiverOwner<A, B>(val first: A, val second: B) {
    fun <T> replace(initial: T, next: T): Pair<A, T> {
        var current = first to initial
        var seen = second
        fun String.replacePair() {
            check(length == 5)
            seen = second
            current = first to next
        }
        "hello".replacePair()
        check(seen == second)
        return current
    }

    fun String.enclosing(): Int {
        var current = 0
        fun Int.replaceWithOwners() {
            check(first != null && second != null)
            current = this + this@enclosing.length
        }
        7.replaceWithOwners()
        return current + length
    }
}

class CapturedLocalExtensionReceiverTests {
    @TestAttribute
    fun mutableCapturePrecedesReceiver() {
        assertEquals(5, capturedExtension())
        fun String.noCapture() = length
        assertEquals(3, "abc".noCapture())
    }

    @TestAttribute
    fun multipleCapturesKeepValueReceiverAndArgumentsDistinct() {
        var current = 0
        var text = ""
        val prefix = "value:"
        fun Int.replaceMultiple(suffix: String) {
            current = this + 2
            text = prefix + this + suffix
        }
        40.replaceMultiple("!")
        assertEquals(42, current)
        assertEquals("value:40!", text)
        context(tag: String)
        fun Int.replaceContext(delta: Int) { current = this + tag.length + delta }
        with("ctx") { 34.replaceContext(5) }
        assertEquals(42, current)
    }

    @TestAttribute
    fun constructedGenericCapturesRetainOwnerAndMethodFrames() {
        assertEquals("owner" to 42, GenericReceiverOwner("owner", true).replace(1, 42))
        assertEquals(7 to "next", GenericReceiverOwner(7, "second").replace("first", "next"))
        assertEquals(42, capturedGenericReceiver(1, 42))
        assertEquals("next", capturedGenericReceiver("first", "next"))
    }

    @TestAttribute
    fun nestedLocalExtensionsKeepBothReceiverIdentities() {
        var current = 0
        fun String.outer() {
            fun Int.inner() { current = this@outer.length * 100 + this }
            7.inner()
            current += length
        }
        "hello".outer()
        assertEquals(512, current)
    }

    @TestAttribute
    fun dispatchAndEnclosingExtensionAreNotTheLocalReceiver() {
        val owner = GenericReceiverOwner("owner", true)
        assertEquals(17, with(owner) { "hello".enclosing() })
    }

    @TestAttribute
    fun nestedCapturesRestoreTheLocalExtensionBinding() {
        var current = 0
        fun String.replaceThroughCaptures() {
            val read = { length }
            val reader = object { fun read() = length }
            fun nested() = length
            current = read() + reader.read() + nested() + length
        }
        "hello".replaceThroughCaptures()
        assertEquals(20, current)
    }

    @TestAttribute
    fun callableReferencesBindCapturedLocalExtensionReceivers() {
        var current = 0
        var receiver = "hello"
        var evaluations = 0
        fun select(): String { evaluations++; return receiver }
        fun String.replaceReference() { current += length }
        val bound: () -> Unit = select()::replaceReference
        val unbound: (String) -> Unit = String::replaceReference
        receiver = "abc"
        assertEquals(1, evaluations)
        bound()
        unbound(receiver)
        assertEquals(8, current)
        assertEquals(1, evaluations)
        bound()
        assertEquals(13, current)
        fun String.noCaptureReference() = length
        val withoutCapture: () -> Int = receiver::noCaptureReference
        val unboundWithoutCapture: (String) -> Int = String::noCaptureReference
        assertEquals(3, withoutCapture())
        assertEquals(5, unboundWithoutCapture("hello"))
    }

    @TestAttribute
    fun recursiveCallsAndReceiverLikeParameterNamesKeepTheirSlots() {
        var current = 0
        fun String.replaceRecursive(__recv0: Int, __self: Int) {
            if (__recv0 > 0) replaceRecursive(__recv0 - 1, __self)
            else current = length + __self
        }
        "hello".replaceRecursive(2, 7)
        assertEquals(12, current)
    }
}
