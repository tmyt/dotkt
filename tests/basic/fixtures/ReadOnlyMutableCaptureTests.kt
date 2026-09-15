package readonlymutablecapture

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrRef
import kotlin.clr.byref
import kotlin.coroutines.*

class ReadOnlyMutableCaptureTests {
    @TestAttribute
    fun deferredReaderInsideCapturingLambdaSharesItsLocal() {
        fun factory(seed: Int): () -> (() -> Int) = {
            var current = seed
            val read = deferGeneric { current }
            current = 2
            read
        }
        assertEquals(2, factory(1)()())
    }

    @TestAttribute
    fun boundedDeferredReaderPreservesConstraints() {
        val initial = CaptureBoundValue()
        val next = CaptureBoundValue()
        check(boundedDeferredRead(initial, next)() === next)
    }

    @TestAttribute
    fun materializedReaderCallsLocalReaderFactory() {
        var current = 1
        fun first(): () -> Int = defer { current }
        val read = defer { first()() }
        current = 2
        assertEquals(2, read())
    }

    @TestAttribute
    fun staticInitializerSharesDeferredLocal() {
        assertEquals(2, staticDeferredReader())
    }

    @TestAttribute
    fun directLocalFunctionSharesManagedReferenceWrites() {
        var current = 7
        fun update() { increment(byref(current)) }
        update()
        assertEquals(12, current)
        fun readAfterUpdate(slot: ClrRef<Int>): Int {
            increment(slot)
            return current
        }
        assertEquals(17, readAfterUpdate(byref(current)))
        assertEquals(17, current)
    }
    @TestAttribute
    fun readersObserveWritesThroughManagedReferences() {
        var current = 7
        val read = { current }
        class Local { fun read(): Int = current }
        val local = Local()
        increment(byref(current))
        assertEquals(12, read())
        assertEquals(12, local.read())
        val update = { increment(byref(current)) }
        update()
        assertEquals(17, current)
        assertEquals(17, read())
        assertEquals(17, local.read())
    }

    @TestAttribute
    fun localClassObservesEnclosingWrites() {
        var current = "initial"
        class Local { fun read(): String = current }
        val first = Local()
        current = "changed"
        val second = Local()
        assertEquals("changed", first.read())
        current = "final"
        assertEquals("final", first.read())
        assertEquals("final", second.read())
    }

    @TestAttribute
    fun anonymousObjectObservesEnclosingWrites() {
        var current = 1
        val reader = object { fun read(): Int = current }
        current = 2
        assertEquals(2, reader.read())
    }

    @TestAttribute
    fun lambdaAndFunctionReferenceShareEnclosingWrites() {
        var current: String? = "initial"
        val lambda = { current }
        fun read(): String? = current
        val reference = ::read
        current = null
        assertEquals(null, lambda())
        assertEquals(null, reference())
        current = "changed"
        assertEquals("changed", lambda())
        assertEquals("changed", read())
        assertEquals("changed", reference())
    }

    @TestAttribute
    fun nestedReadersShareGenericVariable() {
        assertEquals("changed", nestedRead("initial", "changed"))
        assertEquals(2, nestedRead(1, 2))
    }

    @TestAttribute
    fun materializedInlineReadersRetainSharedStorage() {
        var current = 1
        val crossinlineReader = defer { current }
        val noinlineReader = keep { current }
        current = 2
        assertEquals(2, crossinlineReader())
        assertEquals(2, noinlineReader())
    }

    @TestAttribute
    fun genericMaterializedInlineReadersShareOneVariable() {
        assertEquals("changed", deferredRead("initial", "changed"))
        assertEquals(2, deferredRead(1, 2))
        assertEquals(null, deferredRead<String?>("initial", null))
    }

    @TestAttribute
    fun suspendedMaterializedInlineReaderObservesUpdate() {
        assertEquals("changed", suspendedDeferredRead("initial", "changed"))
        assertEquals(2, suspendedDeferredRead(1, 2))
    }

    @TestAttribute
    fun forwardedInlineReadersObserveEnclosingWrites() {
        var current = "initial"
        val read = forwardRead { current }
        current = "changed"
        assertEquals("changed", read())
    }

    @TestAttribute
    fun localFunctionMaterializedReaderObservesEnclosingWrites() {
        var current = "initial"
        fun makeReader(): () -> String = deferGeneric { current }
        val read = makeReader()
        current = "changed"
        assertEquals("changed", read())
        assertEquals("changed", localDeferredRead("initial", "changed"))
        assertEquals(2, localDeferredRead(1, 2))
    }

    @TestAttribute
    fun eachInvocationKeepsItsOwnVariable() {
        fun reader(initial: Int): () -> Int {
            var current = initial
            class Local { fun read(): Int = current }
            val local = Local()
            current += 1
            return { local.read() }
        }
        val first = reader(1)
        val second = reader(10)
        assertEquals(2, first())
        assertEquals(11, second())
    }
}

private fun increment(slot: ClrRef<Int>) { slot.value += 5 }
private interface CaptureBound
private val staticDeferredReader = run {
    var current = 1
    val read = defer { current }
    current = 2
    read
}
private class CaptureBoundValue : CaptureBound
private fun <T : CaptureBound> boundedDeferredRead(initial: T, next: T): () -> T {
    var current = initial
    val read = deferGeneric { current }
    current = next
    return read
}
private inline fun defer(crossinline read: () -> Int): () -> Int = { read() }
private inline fun keep(noinline read: () -> Int): () -> Int = read
private inline fun <T> deferGeneric(crossinline read: () -> T): () -> T = { read() }
private inline fun <T> forwardRead(crossinline read: () -> T): () -> T = deferGeneric { read() }
private fun <T> localDeferredRead(initial: T, next: T): T {
    var current = initial
    fun first(): () -> T = deferGeneric { current }
    fun second(): () -> T {
        fun nested(): () -> T = first()
        return nested()
    }
    val read = second()
    current = next
    check(first()() == next)
    return read()
}
private inline fun <T> deferSuspended(crossinline read: suspend () -> T): suspend () -> T = { read() }
private fun <T> suspendedDeferredRead(initial: T, next: T): T {
    var current = initial
    val read = deferSuspended { current }
    current = next
    var outcome: Result<T>? = null
    read.startCoroutine(object : Continuation<T> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) { outcome = result }
    })
    return outcome!!.getOrThrow()
}
private fun <T> deferredRead(initial: T, next: T): T {
    var current = initial
    val first = deferGeneric { current }
    val second = deferGeneric { current }
    current = next
    check(first() == next)
    return second()
}

private fun <T> nestedRead(initial: T, next: T): T {
    var current = initial
    class Outer {
        fun reader(): () -> T {
            class Inner { fun read(): T = current }
            val inner = Inner()
            return { inner.read() }
        }
    }
    val read = Outer().reader()
    current = next
    return read()
}
