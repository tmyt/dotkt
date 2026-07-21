import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
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
    }
}
