// stackBuffer(n){ buf -> … }: a scoped CLR stack allocation (`localloc`). The block is splice-inlined so the buffer
// lives in the caller's frame; StackBuffer<T> (size/get/set) is erased. Bounds-checked. NOTE: like C#'s own
// stackalloc, the emitted method is intentionally UNVERIFIABLE (localloc) — this sample is exempt from ilverify.
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
}
