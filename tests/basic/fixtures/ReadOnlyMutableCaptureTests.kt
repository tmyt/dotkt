package readonlymutablecapture

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class ReadOnlyMutableCaptureTests {
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
