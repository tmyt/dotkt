import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

private suspend inline fun <T> innerConstructorBoundary(
    crossinline block: (Continuation<T>) -> Unit,
): T = suspendCoroutine { continuation -> block(continuation) }

private class InnerConstructorBatch<T>(private val values: Array<T>) {
    var chosenSize: Int = 0
        private set

    suspend fun collect(useNames: Boolean): List<T> = innerConstructorBoundary { continuation ->
        val nodes = Array(values.size) { Node(continuation) }
        val bundle = if (useNames) Bundle(arrayOf("other")) else Bundle(nodes)
        chosenSize = bundle.size
        continuation.resume(bundle.values())
    }

    private inner class Node(val continuation: Continuation<List<T>>)

    private inner class Bundle {
        val size: Int
        constructor(nodes: Array<Node>) { size = nodes.size }
        constructor(names: Array<String>) { size = -names.size }
        fun values(): List<T> = values.toList()
    }
}

class InnerConstructorIdentityTests {
    @TestAttribute
    fun capturedOuterSelectsSiblingArrayConstructor() {
        val batch = InnerConstructorBatch(arrayOf("left", "right"))
        val result = blockOn { batch.collect(false) }
        assertEquals(2, batch.chosenSize)
        assertEquals(2, result.size)
        assertEquals("left", result[0])
        assertEquals("right", result[1])
    }

    @TestAttribute
    fun erasedArrayOverloadsRetainTheirSelectedDeclarations() {
        val batch = InnerConstructorBatch(arrayOf<Int?>(4, null, 9))
        val result = blockOn { batch.collect(true) }
        assertEquals(-1, batch.chosenSize)
        assertEquals(3, result.size)
        assertEquals(4, result[0])
        assertEquals(null, result[1])
        assertEquals(9, result[2])
    }
}
