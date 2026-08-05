import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import StackBufferInterop.SpanOperations
import kotlin.clr.stackBuffer

class StackBufferTests {
    @TestAttribute
    fun stackAllocationAndSpanInterop() {
        val last = stackBuffer<Int, Int>(5) { buffer ->
            buffer[0] = 1
            for (i in 1 until buffer.size) buffer[i] = buffer[i - 1] * 2
            buffer[buffer.size - 1]
        }
        assertEquals(16, last)

        val total = stackBuffer<Int, Int>(4) { buffer ->
            for (i in 0 until buffer.size) buffer[i] = (i + 1) * (i + 1)
            var sum = 0
            for (i in 0 until buffer.size) sum += buffer[i]
            sum
        }
        assertEquals(30, total)
        val bounds = try {
            stackBuffer<Int, Int>(2) { buffer -> buffer[9] = 1; 0 }
        } catch (e: Exception) {
            -1
        }
        assertEquals(-1, bounds)

        val spanSum = stackBuffer<Int, Int>(4) { buffer ->
            for (i in 0 until buffer.size) buffer[i] = i + 1
            SpanOperations.Sum(buffer.asSpan())
        }
        assertEquals(10, spanSum)

        val filled = stackBuffer<Int, Int>(3) { buffer ->
            SpanOperations.Fill(buffer.asSpan(), 7)
            buffer[0] + buffer[1] + buffer[2]
        }
        assertEquals(21, filled)

        // #86 — a NULLABLE VALUE element. `Span<T>` is a reified generic, so a `Span<Int?>` slot would erase its
        // argument; the stack-buffer path never declares one. `stackBuffer` is inline, so what is emitted is a
        // `localloc` plus a stack pointer whose element token is a SLOT position — the same position a `ref`
        // referent holds in the rule — and `Nullable<int32>` is an unmanaged struct a `localloc` can hold. The
        // token and the allocation therefore agree by construction, which is why the stack node kinds are
        // deliberately absent from the argument-element set. Driven here so a later change that starts declaring a
        // real `Span<T>` slot on this path cannot pass silently.
        val nullableElem = stackBuffer<Int?, Int>(2) { buffer ->
            buffer[0] = 5
            buffer[1] = null
            (buffer[0] ?: 0) + (buffer[1] ?: 100)
        }
        assertEquals(105, nullableElem)
    }
}
