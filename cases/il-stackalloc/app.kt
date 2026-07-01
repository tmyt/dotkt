// stackBuffer(n){ buf -> … }: a scoped CLR stack allocation (`localloc`). The block is splice-inlined so the buffer
// lives in the caller's frame; StackBuffer<T> (size/get/set/asSpan) is erased. Bounds-checked. asSpan() yields a
// real System.Span<T> over the stack memory, passable to .NET Span APIs (which can read AND write it).
// NOTE: like C#'s own stackalloc, the emitted method is intentionally UNVERIFIABLE (localloc) -> exempt from ilverify.
import P.SpanOps
import kotlin.clr.stackBuffer   // stackBuffer/StackBuffer/Span now live in the importable `kotlin.clr` namespace (was the root package)

fun main() {
    val last = stackBuffer<Int, Int>(5) { buf ->
        buf[0] = 1
        for (i in 1 until buf.size) buf[i] = buf[i - 1] * 2   // 1,2,4,8,16
        buf[buf.size - 1]
    }
    println(last)                                             // 16

    val total = stackBuffer<Int, Int>(4) { buf ->
        for (i in 0 until buf.size) buf[i] = (i + 1) * (i + 1) // 1,4,9,16
        var s = 0
        for (i in 0 until buf.size) s += buf[i]
        s
    }
    println(total)                                            // 30

    println(try { stackBuffer<Int, Int>(2) { b -> b[9] = 1; 0 } } catch (e: Exception) { -1 })  // -1 (bounds)

    val sum = stackBuffer<Int, Int>(4) { buf ->
        for (i in 0 until buf.size) buf[i] = i + 1            // 1,2,3,4
        SpanOps.Sum(buf.asSpan())                             // pass the buffer to a .NET Span<int> API
    }
    println(sum)                                              // 10

    val filled = stackBuffer<Int, Int>(3) { buf ->
        SpanOps.Fill(buf.asSpan(), 7)                         // .NET writes into the stack buffer via the Span
        buf[0] + buf[1] + buf[2]
    }
    println(filled)                                           // 21
}
